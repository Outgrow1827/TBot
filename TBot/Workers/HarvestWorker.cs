using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Tbot.Workers {
	public class HarvestWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;

		public HarvestWorker(
			ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) : base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool)_tbotInstance.InstanceSettings.AutoHarvest.Active;
			} catch {
				return false;
			}
		}

		public override string GetWorkerName() => "Harvest";

		public override Feature GetFeature() => Feature.Harvest;

		public override LogSender GetLogSender() => LogSender.Harvest;

		protected override async Task Execute() {
			var stop = false;
			var waitForHarvestReturn = false;

			try {
				if (!(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active)
					return;

				DoLog(LogLevel.Information, "Detecting harvest targets");

				var celestialSnapshot = (_tbotInstance.UserData.celestials ?? new List<Celestial>()).ToList();
				var initialFleets = await RefreshFleets();
				_tbotInstance.UserData.fleets = initialFleets;

				var celestials = await RefreshCelestials(celestialSnapshot);
				_tbotInstance.UserData.celestials = celestials.ToList();

				var slots = await _tbotOgameBridge.UpdateSlots();
				_tbotInstance.UserData.slots = slots ?? new Slots();
				var slotBudget = CalculateSlotBudget(initialFleets, _tbotInstance.UserData.slots);
				var targets = new List<HarvestTarget>();
				var origins = new List<HarvestOriginState>();

				if (slotBudget > 0) {
					targets = await DiscoverTargets(celestials, initialFleets);
					if (targets.Count > 0)
						origins = await BuildOrigins(celestials);
				}

				if (targets.Count == 0)
					DoLog(LogLevel.Information, "Skipping harvest: there are no fields to harvest.");
				if (slotBudget <= 0)
					DoLog(LogLevel.Information, "Skipping harvest: no slots are available.");

				var plan = HarvestPlanner.Build(
					targets,
					origins,
					slotBudget,
					(target, origin) => CalculateRequiredShips(target, origin),
					(target, origin) => CalculateDistance(target, origin));
				if (targets.Count > plan.Count && plan.Count < slotBudget)
					DoLog(LogLevel.Information, $"Skipped {targets.Count - plan.Count} harvest target(s): no usable Recycler/Pathfinder was available on any origin.");

				var sentCount = 0;
				foreach (var assignment in plan) {
					var ships = new Ships();
					ships.SetAmount(assignment.Ship, assignment.ShipsToSend);

					var partialSuffix = assignment.IsPartial
						? $" (partial: {assignment.ShipsToSend}/{assignment.RequiredShips})"
						: string.Empty;
					DoLog(
						LogLevel.Information,
						$"Harvesting debris in {assignment.Target.Destination} from {assignment.Origin} with {assignment.ShipsToSend} {assignment.Ship}{partialSuffix}");

					var fleetId = await _fleetScheduler.SendFleet(
						assignment.Origin,
						ships,
						assignment.Target.Destination,
						Missions.Harvest,
						Speeds.HundredPercent);

					if (fleetId == (int)SendFleetCode.AfterSleepTime) {
						stop = true;
						return;
					}
					if (fleetId == (int)SendFleetCode.NotEnoughSlots) {
						waitForHarvestReturn = true;
						return;
					}
					if (fleetId <= 0)
						continue;

					sentCount++;
					assignment.Origin.Ships.SetAmount(
						assignment.Ship,
						Math.Max(0, assignment.Origin.Ships.GetAmount(assignment.Ship) - assignment.ShipsToSend));
				}

				waitForHarvestReturn = slotBudget <= 0 || (slotBudget > 0 && sentCount >= slotBudget);
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"HandleHarvest exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop) {
						DoLog(LogLevel.Information, "Stopping feature.");
						await EndExecution();
					} else {
						var currentFleets = await RefreshFleets();
						_tbotInstance.UserData.fleets = currentFleets;
						await ScheduleNextExecution(currentFleets, waitForHarvestReturn);
					}

					await _tbotOgameBridge.CheckCelestials();
				}
			}
		}

		private async Task<List<Fleet>> RefreshFleets() {
			return (await _fleetScheduler.UpdateFleets() ?? new List<Fleet>()).ToList();
		}

		private async Task<List<Celestial>> RefreshCelestials(List<Celestial> snapshot) {
			var refreshed = new List<Celestial>(snapshot.Count);
			foreach (var celestial in snapshot) {
				if (celestial == null)
					continue;

				try {
					var updated = celestial;
					if (updated is Planet)
						updated = await _tbotOgameBridge.UpdatePlanet(updated, UpdateTypes.Fast) ?? updated;
					updated = await _tbotOgameBridge.UpdatePlanet(updated, UpdateTypes.Ships) ?? updated;
					refreshed.Add(updated);
				} catch (Exception e) {
					DoLog(LogLevel.Warning, $"Unable to refresh {celestial}: {e.Message}. Keeping the previous state.");
					refreshed.Add(celestial);
				}
			}

			return refreshed;
		}

		private async Task<List<HarvestTarget>> DiscoverTargets(List<Celestial> celestials, List<Fleet> fleets) {
			var targets = new List<HarvestTarget>();
			var activeDestinations = (fleets ?? new List<Fleet>())
				.Where(fleet => fleet?.Mission == Missions.Harvest && fleet.Destination != null)
				.Select(fleet => fleet.Destination)
				.ToList();
			var systemsToScan = BuildSystemsToScan(celestials);
			var systemInfos = new Dictionary<(int Galaxy, int System), GalaxyInfo>();

			foreach (var system in systemsToScan) {
				try {
					var info = await _ogameService.GetGalaxyInfo(system.Galaxy, system.System);
					if (info != null)
						systemInfos[system] = info;
				} catch (Exception e) {
					DoLog(LogLevel.Warning, $"Unable to inspect galaxy {system.Galaxy}:{system.System}: {e.Message}");
				}
			}

			var ownPlanets = celestials
				.OfType<Planet>()
				.Where(planet => planet.Coordinate != null)
				.ToList();
			foreach (var entry in systemInfos) {
				var galaxy = entry.Key.Galaxy;
				var system = entry.Key.System;
				var info = entry.Value;
				if ((bool)_tbotInstance.InstanceSettings.AutoHarvest.HarvestOwnDF) {
					foreach (var ownPlanet in ownPlanets.Where(planet => planet.Coordinate.Galaxy == galaxy && planet.Coordinate.System == system)) {
						var galaxyPlanet = (info.Planets ?? new List<Planet>())
							.FirstOrDefault(planet => planet != null && (planet.ID == ownPlanet.ID || SamePlanetCoordinate(planet, ownPlanet)));
						var debris = galaxyPlanet?.Debris;
						var destination = new Coordinate(ownPlanet.Coordinate.Galaxy, ownPlanet.Coordinate.System, ownPlanet.Coordinate.Position, Celestials.Debris);
						if (debris != null
							&& debris.Resources.TotalResources >= (long)_tbotInstance.InstanceSettings.AutoHarvest.MinimumResourcesOwnDF
							&& !ContainsCoordinate(activeDestinations, destination)) {
							targets.Add(new HarvestTarget(destination, debris.Resources, true));
						}
					}
				}

				if ((bool)_tbotInstance.InstanceSettings.AutoHarvest.HarvestDeepSpace
					&& info.ExpeditionDebris != null
					&& info.ExpeditionDebris.Resources.TotalResources >= (long)_tbotInstance.InstanceSettings.AutoHarvest.MinimumResourcesDeepSpace) {
					var destination = new Coordinate(galaxy, system, 16, Celestials.DeepSpace);
					if (!ContainsCoordinate(activeDestinations, destination))
						targets.Add(new HarvestTarget(destination, info.ExpeditionDebris.Resources));
				}
			}

			return targets
				.GroupBy(target => target.Destination.ToString())
				.Select(group => group.First())
				.ToList();
		}

		private List<(int Galaxy, int System)> BuildSystemsToScan(List<Celestial> celestials) {
			var systems = new HashSet<(int Galaxy, int System)>();
			if (!(bool)_tbotInstance.InstanceSettings.AutoHarvest.HarvestOwnDF
				&& !(bool)_tbotInstance.InstanceSettings.AutoHarvest.HarvestDeepSpace)
				return systems.ToList();

			var planets = celestials
				.Where(celestial => celestial is Planet && celestial.Coordinate != null)
				.Select(celestial => celestial.Coordinate)
				.ToList();
			if (planets.Count == 0) {
				planets = celestials
					.Where(celestial => celestial?.Coordinate != null)
					.Select(celestial => celestial.Coordinate)
					.ToList();
			}

			var harvestDeepSpace = (bool)_tbotInstance.InstanceSettings.AutoHarvest.HarvestDeepSpace;
			var split = harvestDeepSpace && (bool)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Active;
			var range = split ? Math.Max(0, (int)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Range) : 0;
			foreach (var coordinate in planets) {
				for (var offset = -range; offset <= range; offset++)
					systems.Add((coordinate.Galaxy, GeneralHelper.WrapSystem(coordinate.System + offset)));
			}

			return systems.ToList();
		}

		private async Task<List<HarvestOriginState>> BuildOrigins(List<Celestial> celestials) {
			var origins = celestials
				.Where(celestial => celestial?.Coordinate != null && celestial.Ships != null)
				.Select(celestial => new HarvestOriginState(celestial))
				.ToList();
			if (origins.Count == 0)
				return origins;

			var bonusOrigin = await _tbotOgameBridge.UpdatePlanet(origins[0].Celestial, UpdateTypes.LFBonuses);
			var bonuses = bonusOrigin?.LFBonuses ?? new LFBonuses();
			foreach (var origin in origins)
				origin.Celestial.LFBonuses = bonuses;

			return origins;
		}

		private long CalculateRequiredShips(HarvestTarget target, HarvestOriginState origin) {
			var ship = target.IsOwnDebris ? Buildables.Recycler : Buildables.Pathfinder;
			var cargoBonus = origin.Celestial.LFBonuses?.GetShipCargoBonus(ship) ?? 0;
			return _calculationService.CalcShipNumberForPayload(
				target.Resources,
				ship,
				_tbotInstance.UserData.researches.HyperspaceTechnology,
				_tbotInstance.UserData.serverData,
				cargoBonus,
				_tbotInstance.UserData.userInfo.Class,
				_tbotInstance.UserData.serverData.ProbeCargo);
		}

		private int CalculateDistance(HarvestTarget target, HarvestOriginState origin) {
			return _calculationService.CalcDistance(
				origin.Celestial.Coordinate,
				target.Destination,
				_tbotInstance.UserData.serverData);
		}

		private int CalculateSlotBudget(List<Fleet> fleets, Slots slots) {
			var rankSlotsPriority = new List<RankSlotsPriority> {
				new RankSlotsPriority(
					Feature.BrainAutoMine,
					(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
					(bool)_tbotInstance.InstanceSettings.Brain.Active
						&& (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active
						&& ((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active
							|| (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active
							|| (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active
							|| (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active),
					(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
					(fleets ?? new List<Fleet>()).Count(fleet => fleet.Mission == Missions.Transport)),
				new RankSlotsPriority(
					Feature.Expeditions,
					(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
					(bool)_tbotInstance.InstanceSettings.Expeditions.Active,
					slots?.ExpTotal ?? 0,
					slots?.ExpInUse ?? 0),
				new RankSlotsPriority(
					Feature.AutoFarm,
					(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
					(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
					(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
					(fleets ?? new List<Fleet>()).Count(fleet => fleet.Mission == Missions.Attack)),
				new RankSlotsPriority(
					Feature.Colonize,
					(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
					(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
					(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active
						? (int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots
						: 1,
					(fleets ?? new List<Fleet>()).Count(fleet => fleet.Mission == Missions.Colonize)),
				new RankSlotsPriority(
					Feature.AutoDiscovery,
					(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
					(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
					(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
					(fleets ?? new List<Fleet>()).Count(fleet => fleet.Mission == Missions.Discovery)),
				new RankSlotsPriority(
					Feature.Harvest,
					(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
					(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
					(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
					(fleets ?? new List<Fleet>()).Count(fleet => fleet.Mission == Missions.Harvest))
			};

			return Math.Max(0, _calculationService.CalcSlotsPriority(
				Feature.Harvest,
				rankSlotsPriority,
				slots ?? new Slots(),
				fleets ?? new List<Fleet>(),
				(int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree));
		}

		private async Task ScheduleNextExecution(List<Fleet> fleets, bool waitForHarvestReturn) {
			var harvestFleets = (fleets ?? new List<Fleet>())
				.Where(fleet => fleet?.Mission == Missions.Harvest)
				.ToList();
			long interval;
			if (waitForHarvestReturn && harvestFleets.Count > 0) {
				interval = Math.Max(1000, (harvestFleets.Min(fleet => fleet.BackIn ?? 0) * 1000)
					+ RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds));
			} else {
				interval = RandomizeHelper.CalcRandomInterval(
					(int)_tbotInstance.InstanceSettings.AutoHarvest.CheckIntervalMin,
					(int)_tbotInstance.InstanceSettings.AutoHarvest.CheckIntervalMax);
			}

			if (interval <= 0)
				interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);

			ChangeWorkerPeriod(interval);
			var time = await _tbotOgameBridge.GetDateTime();
			DoLog(LogLevel.Information, $"Next check at {time.AddMilliseconds(interval)}");
		}

		private static bool SamePlanetCoordinate(Planet first, Planet second) {
			return first?.Coordinate != null
				&& second?.Coordinate != null
				&& first.Coordinate.Galaxy == second.Coordinate.Galaxy
				&& first.Coordinate.System == second.Coordinate.System
				&& first.Coordinate.Position == second.Coordinate.Position;
		}

		private static bool ContainsCoordinate(IEnumerable<Coordinate> coordinates, Coordinate candidate) {
			return (coordinates ?? Enumerable.Empty<Coordinate>()).Any(coordinate => coordinate?.IsSame(candidate) == true);
		}
	}
}
