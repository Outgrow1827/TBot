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
			bool stop = false;
			int skips = 0;
			var rand = new Random();
			try
			{
				if (_tbotInstance.UserData.discoveryBlackList == null)
				{
					_tbotInstance.UserData.discoveryBlackList = new Dictionary<Coordinate, DateTime>();
				}
				if (!_tbotInstance.UserData.isSleeping)
				{
					DoLog(LogLevel.Information, $"Starting AutoDiscovery...");
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					List<RankSlotsPriority> rankSlotsPriority = new()
					{
						new RankSlotsPriority(Feature.BrainAutoMine,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
							((bool)_tbotInstance.InstanceSettings.Brain.Active && (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active && ((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active || (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
							(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots),
						new RankSlotsPriority(Feature.Expeditions,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
							(bool)_tbotInstance.InstanceSettings.Expeditions.Active,
							(int)_tbotInstance.UserData.slots.ExpTotal),
						new RankSlotsPriority(Feature.AutoFarm,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
							(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
							(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots),
						new RankSlotsPriority(Feature.Colonize,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
							(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
							(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
								(int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots :
								1),
						new RankSlotsPriority(Feature.AutoDiscovery,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
							(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
							(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots),
						new RankSlotsPriority(Feature.Harvest,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
							(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
							(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots)
					};
					int MaxSlots = _calculationService.CalcSlotsPriority(Feature.AutoDiscovery, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree);

					Celestial origin = _tbotInstance.UserData.celestials
						.Unique()
						.Where(c => c.Coordinate.Galaxy == (int)_tbotInstance.InstanceSettings.AutoDiscovery.Origin.Galaxy)
						.Where(c => c.Coordinate.System == (int)_tbotInstance.InstanceSettings.AutoDiscovery.Origin.System)
						.Where(c => c.Coordinate.Position == (int)_tbotInstance.InstanceSettings.AutoDiscovery.Origin.Position)
						.Where(c => c.Coordinate.Type == Enum.Parse<Celestials>((string)_tbotInstance.InstanceSettings.AutoDiscovery.Origin.Type))
						.SingleOrDefault() ?? new() { ID = 0 };
					if (origin.ID == 0)
					{
						stop = true;
						DoLog(LogLevel.Warning, "Unable to parse AutoDiscovery origin");
						return;
					}

					if ((bool)_tbotInstance.InstanceSettings.SleepMode.Active)
					{
						DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.GoToSleep, out DateTime goToSleep);
						DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.WakeUp, out DateTime wakeUp);
						DateTime time = await _tbotOgameBridge.GetDateTime();
						if (GeneralHelper.ShouldSleep(time, goToSleep, wakeUp))
						{
							DoLog(LogLevel.Warning, "Unable to send discovery fleet: bed time has passed");
							stop = true;
							return;
						}
					}

					List<Coordinate> possibleDestinations = new();
					int discoveries = await _ogameService.GetAvailableDiscoveries(origin);
					if (discoveries > 0)
					{
						for (int i = 1; i <= _tbotInstance.UserData.serverData.Systems; i++)
						{
							var newDestinations = await _ogameService.GetPositionsAvailableForDiscoveryFleet(origin, new Coordinate() { Galaxy = origin.Coordinate.Galaxy, System = i, Position = 1 });
							possibleDestinations.AddRange(newDestinations);
						}
					}
					possibleDestinations = possibleDestinations
						.Shuffle()
						.OrderBy(c => c.Position)
						.OrderBy(c => c.System)
						.OrderBy(c => _calculationService.CalcDistance(origin.Coordinate, c, _tbotInstance.UserData.serverData))
						.ToList();

					while (possibleDestinations.Count > 0 && _tbotInstance.UserData.fleets.Where(s => s.Mission == Missions.Discovery).Count() < MaxSlots && _tbotInstance.UserData.slots.Free > (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree)
					{
						Coordinate dest = possibleDestinations.First();
						possibleDestinations.Remove(dest);

						Coordinate blacklistedCoord = _tbotInstance.UserData.discoveryBlackList.Keys
							.Where(c => c.Galaxy == dest.Galaxy && c.System == dest.System && c.Position == dest.Position)
							.SingleOrDefault();

						if (blacklistedCoord != null)
						{
							if (_tbotInstance.UserData.discoveryBlackList[blacklistedCoord] > DateTime.Now)
							{
								skips++;
								continue;
							}
							else
							{
								_tbotInstance.UserData.discoveryBlackList.Remove(blacklistedCoord);
							}
						}

						var result = await _ogameService.SendDiscovery(origin, dest);
						if (!result)
						{
							DoLog(LogLevel.Warning, $"Failed to send discovery fleet to {dest.ToString()} from {origin.ToString()}.");
							_tbotInstance.UserData.discoveryBlackList.Add(dest, DateTime.Now.AddDays(1));
						}
						else
						{
							DoLog(LogLevel.Information, $"Discovery fleet sent to {dest.ToString()} from {origin.ToString()}.");
							_tbotInstance.UserData.discoveryBlackList.Add(dest, DateTime.Now.AddDays(7));
						}

						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						if (_tbotInstance.UserData.slots.Free <= _tbotInstance.InstanceSettings.General.SlotsToLeaveFree || _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery) >= MaxSlots)
						{
							long interval = 0;
							if (_tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Discovery).Any())
							{
								interval = (_tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Discovery).Max(f => f.BackIn) ?? 0) * 1000;
							}

							if (interval <= 0)
							{
								interval = Random.Shared.Next((int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMin, (int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMax) * 1000;
							}

							DoLog(LogLevel.Information, $"No more fleet slots available or max discovery fleets sent. Delaying for {TimeSpan.FromMilliseconds(interval).TotalSeconds}s.");
							await Task.Delay((int)interval);
							// break; // https://discord.com/channels/801453618770214923/919312220637249537/1402221089588903968
						}
						await Task.Delay(Random.Shared.Next((int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMin, (int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMax) * 1000);
					}
					if (skips > 0)
					{
						DoLog(LogLevel.Information, $"{skips} systems skipped (blacklisted)");
					}
					if (possibleDestinations.Count == 0)
					{
						DoLog(LogLevel.Information, "No more systems to discover: stopping for now.");
						stop = true;
					}
				}
				else
				{
					DoLog(LogLevel.Information, "Skipping: Sleep Mode is active.");
					stop = true;
				}
			}
			catch (Exception e)
			{
				DoLog(LogLevel.Error, $"AutoDiscovery Exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			}
			finally
			{
				if (stop)
				{
					DoLog(LogLevel.Information, "Stopping AutoDiscovery.");
					await _tbotInstance.StopFeature(GetFeature());
				}
				
				else
				{
					var time = Random.Shared.Next((int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMin, (int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMax);
					DoLog(LogLevel.Information, $"Next AutoDiscovery check in {time}s.");
					await Task.Delay(time * 1000);
				}
				await _tbotOgameBridge.CheckCelestials();
			}			
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
