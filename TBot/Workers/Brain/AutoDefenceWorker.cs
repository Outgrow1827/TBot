using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers.Brain {
	public class AutoDefenceWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		public AutoDefenceWorker(ITBotMain parentInstance,
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
			try {
				DoLog(LogLevel.Information, "Running autodefence...");

				List<Celestial> newCelestials = _tbotInstance.UserData.celestials.ToList();
				List<Celestial> celestialsToExclude = _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.Brain.AutoDefence.Exclude, _tbotInstance.UserData.celestials);

				Defences neededDefences = new(
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.RocketLauncher,
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.LightLaser,
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.HeavyLaser,
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.GaussCannon,
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.IonCannon,
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.PlasmaTurret,
					(long) ((bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.SmallShieldDome ? 1: 0),
					(long) ((bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.LargeShieldDome ? 1: 0),
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.AntiBallisticMissiles,
					(long) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.InterplanetaryMissiles
				);
				foreach (Celestial celestial in (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.RandomOrder ? _tbotInstance.UserData.celestials.Shuffle().ToList() : _tbotInstance.UserData.celestials) {
					if (celestialsToExclude.Has(celestial)) {
						DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: celestial in exclude list.");
						continue;
					}

					var tempCelestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);

					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
					if (tempCelestial.HasProduction()) {
						DoLog(LogLevel.Warning, $"Skipping {tempCelestial.ToString()}: there is already a production ongoing.");
						foreach (Production production in tempCelestial.Productions) {
							Buildables productionType = (Buildables) production.ID;
							DoLog(LogLevel.Information, $"Skipping {tempCelestial.ToString()}: {production.Nbr}x{productionType.ToString()} are already in production.");
						}
						continue;
					}
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Constructions);
					if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
						Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
						DoLog(LogLevel.Information, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
						continue;
					}

					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Defences);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);

					var capacity = _calculationService.CalcFleetCapacity(tempCelestial.Ships, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
					if (tempCelestial.Coordinate.Type == Celestials.Moon && (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.ExcludeMoons) {
						DoLog(LogLevel.Information, $"Skipping {tempCelestial.ToString()}: celestial is a moon.");
						continue;
					}
					Defences currentDefences = tempCelestial.Defences;
					Defences defencesToBuild = neededDefences.Difference(currentDefences);
					if (defencesToBuild.IsEmpty()) {
						DoLog(LogLevel.Information, $"Skipping {tempCelestial.ToString()}: all defences are already built.");
						continue;
					} else {
						Resources defenceCost = defencesToBuild.GetDefenceCost();
						if (!tempCelestial.Resources.IsEnoughFor(defenceCost)) {
							DoLog(LogLevel.Information, $"{tempCelestial.ToString()}: Not enough resources to build all defences. Will build only what is possible.");
							defencesToBuild = _calculationService.CalcmaxDefencesBuildable(defencesToBuild, tempCelestial.Resources);
						}
						if (defencesToBuild.IsEmpty()) {
							DoLog(LogLevel.Information, $"{tempCelestial.ToString()}: Not enough resources to build any defence.");
							continue;
						}
						DoLog(LogLevel.Information, $"{tempCelestial.ToString()}: Defences to build: {defencesToBuild.ToString()}");
						try {
							foreach (var (defenceType, amountNeeded) in defencesToBuild.GetDefenceTypesWithAmount()) {
								await _ogameService.BuildDefences(tempCelestial, defenceType, amountNeeded);
							}
							DoLog(LogLevel.Information, "Production succesfully started.");
						} catch {
							DoLog(LogLevel.Warning, "Unable to start defence production.");
						}
						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
						foreach (Production production in tempCelestial.Productions) {
							Buildables productionType = (Buildables) production.ID;
							DoLog(LogLevel.Information, $"{tempCelestial.ToString()}: {production.Nbr}x{productionType.ToString()} are in production.");
						}
					}

					newCelestials.Remove(celestial);
					newCelestials.Add(tempCelestial);
				}
				_tbotInstance.UserData.celestials = newCelestials;
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"Unable to complete autodefence: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				var time = await _tbotOgameBridge.GetDateTime();
				var interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Brain.AutoDefence.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Brain.AutoDefence.CheckIntervalMax);
				if (interval <= 0)
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
				var newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next Defence check at {newTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
			}
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return ((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.Active);
			} catch(Exception) {
				return false;
			}
		}

		public override string GetWorkerName() {
			return "AutoDefence";
		}
		public override Feature GetFeature() {
			return Feature.BrainAutobuildDefence;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoDefence;
		}
	}
}
