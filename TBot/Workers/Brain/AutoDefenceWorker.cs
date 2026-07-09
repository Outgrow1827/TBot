using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Common.Settings;
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

		// Ship types whose (Metal+Crystal) cost feeds into the debris-field value of a stationed fleet -
		// same set used by the "Optimal Defense" calculator (Vesselin Bontchev) this worker's
		// production-based mode is modeled after. Deuterium is excluded because debris fields never
		// contain deuterium in OGame.
		private static readonly Buildables[] DebrisShipTypes = {
			Buildables.SmallCargo, Buildables.LargeCargo, Buildables.LightFighter, Buildables.HeavyFighter,
			Buildables.Cruiser, Buildables.Battleship, Buildables.ColonyShip, Buildables.Recycler,
			Buildables.EspionageProbe, Buildables.Bomber, Buildables.SolarSatellite, Buildables.Destroyer,
			Buildables.Deathstar, Buildables.Battlecruiser
		};

		// The 5 defence types the production-based formula computes. AntiBallisticMissiles and the shield
		// domes aren't part of the "Optimal Defense" calculation - those always come from the manually
		// configured DefenceToReach, regardless of this toggle.
		private static readonly Buildables[] FormulaCoveredTypes = {
			Buildables.RocketLauncher, Buildables.LightLaser, Buildables.HeavyLaser,
			Buildables.GaussCannon, Buildables.PlasmaTurret
		};

		private const float PlunderPercent = 75f;
		// Fallback only used if Brain.AutoDefence.ProductionCoverageHours isn't set in the JSON -
		// keeps old installs behaving the same as before this became configurable.
		private const int DefaultProductionCoverageHours = 24;

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
				bool useProductionBased = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.AutoDefence, "UseProductionBasedCalculation")
					&& (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.UseProductionBasedCalculation;

				foreach (Celestial celestial in (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.RandomOrder ? _tbotInstance.UserData.celestials.Shuffle().ToList() : _tbotInstance.UserData.celestials) {
					if (celestialsToExclude.Has(celestial)) {
						DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: celestial in exclude list.");
						continue;
					}
					if (celestial.Coordinate.Type == Celestials.Moon && (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.ExcludeMoons) {
						DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: celestial is a moon.");
						continue;
					}

					var tempCelestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);

					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
					if (tempCelestial.HasProduction()) {
						DoLog(LogLevel.Information, $"Skipping {tempCelestial.ToString()}: there is already a production ongoing.");
						newCelestials.Remove(celestial);
						newCelestials.Add(tempCelestial);
						continue;
					}
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Constructions);
					if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
						Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
						DoLog(LogLevel.Information, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
						newCelestials.Remove(celestial);
						newCelestials.Add(tempCelestial);
						continue;
					}

					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Defences);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Facilities);

					Dictionary<Buildables, long> targets;
					if (useProductionBased && tempCelestial is Planet) {
						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Buildings);
						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
						targets = CalcNeededDefenceFromProduction((Planet) tempCelestial);
						DoLog(LogLevel.Information, $"{tempCelestial}: production-based defence targets - " +
							$"RocketLauncher:{targets[Buildables.RocketLauncher]} LightLaser:{targets[Buildables.LightLaser]} " +
							$"HeavyLaser:{targets[Buildables.HeavyLaser]} GaussCannon:{targets[Buildables.GaussCannon]} " +
							$"PlasmaTurret:{targets[Buildables.PlasmaTurret]}");
					} else {
						targets = new Dictionary<Buildables, long>();
						foreach (var buildable in FormulaCoveredTypes) {
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach, buildable.ToString())) {
								targets[buildable] = (long) (int) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach[buildable.ToString()];
							}
						}
					}
					// AntiBallisticMissiles is not part of the "Optimal Defense" formula (it isn't a
					// combat-value defence, it exists to shoot down incoming IPMs) - always manual, regardless of the toggle.
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach, "AntiBallisticMissiles")) {
						targets[Buildables.AntiBallisticMissiles] = (long) (int) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach.AntiBallisticMissiles;
					}

					// Cheapest unit cost first, until the celestial's current resources run out.
					var orderedTypes = targets.Keys
						.OrderBy(b => {
							var unitCost = _calculationService.CalcPrice(b, 1);
							return unitCost.Metal + unitCost.Crystal + unitCost.Deuterium;
						})
						.ToList();

					var availableResources = new Resources {
						Metal = tempCelestial.Resources.Metal,
						Crystal = tempCelestial.Resources.Crystal,
						Deuterium = tempCelestial.Resources.Deuterium
					};
					bool builtSomething = false;
					foreach (var buildable in orderedTypes) {
						long deficit = targets[buildable] - tempCelestial.Defences.GetAmount(buildable);
						if (deficit <= 0)
							continue;

						long buildableCount = Math.Min(deficit, _calculationService.CalcMaxBuildableNumber(buildable, availableResources));
						if (buildableCount <= 0)
							continue;

						// Cap the order so it doesn't queue longer than MaxConstructionTime (minutes) worth
						// of build time - without this, a big deficit (e.g. after raising DefenceToReach)
						// could queue a single order that ties up the celestial's production queue for days.
						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.AutoDefence, "MaxConstructionTime") && (int) _tbotInstance.InstanceSettings.Brain.AutoDefence.MaxConstructionTime > 0) {
							long maxSeconds = (long) _tbotInstance.InstanceSettings.Brain.AutoDefence.MaxConstructionTime * 60;
							long estimatedSeconds = _calculationService.CalcProductionTime(buildable, (int) buildableCount, _tbotInstance.UserData.serverData, tempCelestial.Facilities);
							if (estimatedSeconds > maxSeconds && estimatedSeconds > 0) {
								long cappedCount = Math.Max(1, buildableCount * maxSeconds / estimatedSeconds);
								DoLog(LogLevel.Information, $"{tempCelestial}: capping {buildable} order from {buildableCount} to {cappedCount} - would take longer than MaxConstructionTime ({_tbotInstance.InstanceSettings.Brain.AutoDefence.MaxConstructionTime} min).");
								buildableCount = cappedCount;
							}
						}

						var cost = _calculationService.CalcPrice(buildable, (int) buildableCount);
						DoLog(LogLevel.Information, $"{tempCelestial}: building {buildableCount}x{buildable} (deficit was {deficit}).");
						await _ogameService.BuildDefences(tempCelestial, buildable, buildableCount);
						availableResources.Metal -= cost.Metal;
						availableResources.Crystal -= cost.Crystal;
						availableResources.Deuterium -= cost.Deuterium;
						builtSomething = true;
						// OGame only lets a celestial queue one defence build order at a time (same
						// production queue as ships) - stop after the first successful order this cycle.
						break;
					}

					// Shield domes are single-unit bool targets, never covered by the production formula.
					foreach (var buildable in new[] { Buildables.SmallShieldDome, Buildables.LargeShieldDome }) {
						if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach, buildable.ToString()))
							continue;
						bool wanted = (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.DefenceToReach[buildable.ToString()];
						if (!wanted || tempCelestial.Defences.GetAmount(buildable) > 0)
							continue;
						var cost = _calculationService.CalcPrice(buildable, 1);
						if (!availableResources.IsEnoughFor(cost))
							continue;
						DoLog(LogLevel.Information, $"{tempCelestial}: building {buildable}.");
						await _ogameService.BuildDefences(tempCelestial, buildable, 1);
						builtSomething = true;
						break;
					}

					if (!builtSomething) {
						DoLog(LogLevel.Information, $"{tempCelestial}: defences already at target, nothing to build.");
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
				DoLog(LogLevel.Information, $"Next autodefence check at {newTime}");
				await _tbotOgameBridge.CheckCelestials();
			}
		}

		// Ports the "Optimal Defense" formula (Vesselin Bontchev, bontchev.my.contact.bg/ogame/optimaldefense.html):
		// defence needed to absorb HoursOfProductionToCover worth of production + current stock + the
		// debris value of the fleet sitting on the celestial, assuming an attacker loots PlunderPercent%
		// of it. Cascades from Plasma Turret down to Rocket Launcher/Light Laser, each tier covering
		// whatever loot value the tier(s) above it couldn't.
		private Dictionary<Buildables, long> CalcNeededDefenceFromProduction(Planet planet) {
			var researches = _tbotInstance.UserData.researches;
			var speed = (int) _tbotInstance.UserData.serverData.Speed;
			var playerClass = _tbotInstance.UserData.userInfo.Class;
			var hasGeologist = _tbotInstance.UserData.staff.Geologist;
			var hasStaff = _tbotInstance.UserData.staff.IsFull;

			long hourlyMetal = _calculationService.CalcMetalProduction(planet, speed, 1, researches, playerClass, hasGeologist, hasStaff);
			long hourlyCrystal = _calculationService.CalcCrystalProduction(planet, speed, 1, researches, playerClass, hasGeologist, hasStaff);
			long hourlyDeuterium = _calculationService.CalcDeuteriumProduction(planet, speed, 1, researches, playerClass, hasGeologist, hasStaff);

			// CalcDeuteriumProduction only accounts for the Deuterium Synthesizer's own output - it doesn't
			// net out what the Fusion Reactor burns. Same formula the reference calculator uses.
			if (planet.Buildings.FusionReactor > 0) {
				long fusionConsumption = (long) Math.Round(10 * planet.Buildings.FusionReactor * Math.Pow(1.1, planet.Buildings.FusionReactor));
				hourlyDeuterium -= Math.Min(fusionConsumption, hourlyDeuterium);
			}

			int coverageHours = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.AutoDefence, "ProductionCoverageHours") && (int) _tbotInstance.InstanceSettings.Brain.AutoDefence.ProductionCoverageHours > 0
				? (int) _tbotInstance.InstanceSettings.Brain.AutoDefence.ProductionCoverageHours
				: DefaultProductionCoverageHours;
			long productionOverPeriod = (hourlyMetal + hourlyCrystal + hourlyDeuterium) * coverageHours;
			long stored = planet.Resources.Metal + planet.Resources.Crystal + planet.Resources.Deuterium;

			float debrisFactor = _tbotInstance.UserData.serverData.DebrisFactor; // fraction, e.g. 0.3 for 30%
			long fleetDebrisValue = 0;
			foreach (var shipType in DebrisShipTypes) {
				long count = planet.Ships.GetAmount(shipType);
				if (count <= 0)
					continue;
				var shipCost = _calculationService.CalcPrice(shipType, 1);
				fleetDebrisValue += (shipCost.Metal + shipCost.Crystal) * count;
			}
			fleetDebrisValue = (long) (fleetDebrisValue * debrisFactor);

			double totalLoot = fleetDebrisValue + (productionOverPeriod + stored) * (PlunderPercent / 100.0);
			double debrisPercent = debrisFactor * 100.0;
			double debrisRatio = (100.0 - debrisPercent) / 100.0;

			long neededPT = (long) Math.Ceiling(5.0658556 * totalLoot * (70.0 / (100.0 - debrisPercent)) / 100000.0);
			long neededGC = Math.Max(0, (long) Math.Ceiling(totalLoot / (10000.0 * debrisRatio) - neededPT));
			long neededHL = Math.Max(0, (long) Math.Ceiling((totalLoot / (4000.0 * debrisRatio) - neededPT - neededGC) / 0.6));
			long neededRlPlusLl = Math.Max(0, (long) Math.Ceiling(totalLoot / (1000.0 * debrisRatio) - neededPT - neededGC - neededHL));
			long neededRL = (long) Math.Ceiling(neededRlPlusLl * 2.0 / 3.0);
			long neededLL = (long) Math.Ceiling(neededRlPlusLl / 3.0);

			return new Dictionary<Buildables, long> {
				[Buildables.RocketLauncher] = neededRL,
				[Buildables.LightLaser] = neededLL,
				[Buildables.HeavyLaser] = neededHL,
				[Buildables.GaussCannon] = neededGC,
				[Buildables.PlasmaTurret] = neededPT,
			};
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return ((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.AutoDefence.Active);
			} catch (Exception) {
				return false;
			}
		}

		public override string GetWorkerName() {
			return "AutoDefence";
		}
		public override Feature GetFeature() {
			return Feature.BrainAutoDefence;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoDefence;
		}
	}
}
