using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	public class AutoDiscoveryWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		public AutoDiscoveryWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		protected override async Task Execute() {
			bool delay = false;
			bool stop = false;
			int failures = 0;
			int skips = 0;
			var rand = new Random();
			try {
				if (_tbotInstance.UserData.discoveryBlackList == null) {
					_tbotInstance.UserData.discoveryBlackList = new Dictionary<Coordinate, DateTime>();
				}
				if (!_tbotInstance.UserData.isSleeping) {
					DoLog(LogLevel.Information, $"Starting AutoDiscovery...");
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					var origins = _calculationService.ParseCelestialsList(
						_tbotInstance.InstanceSettings.AutoDiscovery.Origin, 
						_tbotInstance.UserData.celestials
					);
					if (origins.Count == 0) {
						stop = true;
						DoLog(LogLevel.Warning, "No valid AutoDiscovery origins");
						return;
					}
					var selectedOrigin = GetBestOrigin(origins, new Coordinate { Galaxy = 1, System = 1, Position = 1 });

					if (selectedOrigin == null) {
						DoLog(LogLevel.Warning, "No valid origin found.");
						return;
					}

					List<Coordinate> possibleDestinations = new();
					for (int i = 1; i <= _tbotInstance.UserData.serverData.Systems; i++) {
						for (int j = 1; j <= 15; j++) {
							possibleDestinations.Add(new Coordinate() {
								Galaxy = selectedOrigin.Coordinate.Galaxy,
								System = i,
								Position = j
							});
						}
					}
					possibleDestinations = possibleDestinations
						.Shuffle()
						.OrderBy(c => _calculationService.CalcDistance(selectedOrigin.Coordinate, c, _tbotInstance.UserData.serverData))
						.ToList();

					while (possibleDestinations.Count > 0 && _tbotInstance.UserData.fleets.Where(s => s.Mission == Missions.Discovery).Count() < (int) _tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots && _tbotInstance.UserData.slots.Free > (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
						Coordinate dest = possibleDestinations.First();
						possibleDestinations.Remove(dest);
						Coordinate blacklistedCoord = _tbotInstance.UserData.discoveryBlackList.Keys
							.Where(c => c.Galaxy == dest.Galaxy)
							.Where(c => c.System == dest.System)
							.Where(c => c.Position == dest.Position)
							.SingleOrDefault() ?? null;

						if (blacklistedCoord != null) {
							if (_tbotInstance.UserData.discoveryBlackList.Single(d => d.Key.Galaxy == dest.Galaxy && d.Key.System == dest.System && d.Key.Position == dest.Position).Value > DateTime.Now) {
								skips++;
								if (skips >= _tbotInstance.UserData.serverData.Systems * 15) {
									DoLog(LogLevel.Information, $"Galaxy depleted: stopping");
									stop = true;
									break;
								} else {
									continue;
								}
							} else {
								_tbotInstance.UserData.discoveryBlackList.Remove(blacklistedCoord);
							}
						}

						selectedOrigin = await _tbotOgameBridge.UpdatePlanet(selectedOrigin, UpdateTypes.Resources);
						if (!selectedOrigin.Resources.IsEnoughFor(new Resources { Metal = 5000, Crystal = 1000, Deuterium = 500 })) {
							DoLog(LogLevel.Warning, $"Failed to send discovery fleet from {selectedOrigin.ToString()}: not enough resources.");
							return;
						}

						var result = await _ogameService.SendDiscovery(selectedOrigin, dest);
						if (!result) {
							failures++;
							DoLog(LogLevel.Warning, $"Failed to send discovery fleet to {dest.ToString()} from {selectedOrigin.ToString()}.");
							_tbotInstance.UserData.discoveryBlackList.Add(dest, DateTime.Now.AddDays(1));
						} else {
							DoLog(LogLevel.Information, $"Sent discovery fleet to {dest.ToString()} from {selectedOrigin.ToString()}.");
							_tbotInstance.UserData.discoveryBlackList.Add(dest, DateTime.Now.AddDays(7));
						}

						if (failures >= (int) _tbotInstance.InstanceSettings.AutoDiscovery.MaxFailures) {
							DoLog(LogLevel.Warning, $"Max failures reached");
							break;
						}

						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						if (_tbotInstance.UserData.slots.Free <= 1) {
							DoLog(LogLevel.Information, $"AutoDiscoveryWorker: No slots left, delaying");
							delay = true;
							break;
						}
					}
				} else {
					stop = true;
				}
			} catch (Exception ex) {
				DoLog(LogLevel.Error, "AutoDiscovery exception");
				DoLog(LogLevel.Warning, ex.ToString());
			} finally {
				if (stop) {
					DoLog(LogLevel.Information, $"Stopping feature.");
					await EndExecution();
				} else {
					long interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMax);
					if (delay) {
						DoLog(LogLevel.Information, $"Delaying...");
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						interval = (_tbotInstance.UserData.fleets.OrderBy(f => f.BackIn).First().BackIn ?? 0) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
					}
					if (interval <= 0)
						interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
					var time = await _tbotOgameBridge.GetDateTime();
					var newTime = time.AddMilliseconds(interval);
					ChangeWorkerPeriod(interval);
					DoLog(LogLevel.Information, $"Next AutoDiscovery check at {newTime.ToString()}");
				}
				await _tbotOgameBridge.CheckCelestials();
			}
		}
		private Celestial GetBestOrigin(List<Celestial> origins, Coordinate dest) {
			return origins
				.OrderBy(o => _calculationService.CalcDistance(o.Coordinate, dest, _tbotInstance.UserData.serverData))
				.FirstOrDefault(o => o.Resources.IsEnoughFor(new Resources { Metal = 5000, Crystal = 1000, Deuterium = 500 }));
}
		public override bool IsWorkerEnabledBySettings() {
			try {
				return 
					(bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active
				;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "AutoDiscovery";
		}
		public override Feature GetFeature() {
			return Feature.AutoDiscovery;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoDiscovery;
		}
	}
}
