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

namespace Tbot.Workers {
	public class HarvestWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;

		// In-process cache for AutoHarvest.ScanRange galaxy lookups, same pattern as ColonizeWorker's
		// _galaxyScanCache - avoids re-fetching the same system's full page more than once per
		// staleness window within a single run. The debris sighting HISTORY itself (which positions
		// keep producing debris) lives in _harvestCache (SQLite, see TBotDataCache.cs) so it
		// survives a bot restart instead of resetting every time. Restored 2026-08-24 - this whole
		// block (TBotDataCache wiring, ScanRange, retry/pacing) existed as uncommitted work before
		// and was lost in an earlier reset; recovered from C:\github\TBot.backup\TBot\Workers\HarvestWorker.cs
		// and re-merged onto the newer HarvestPlanner-based Execute()/DiscoverTargets() below.
		private static readonly Dictionary<string, (GalaxyInfo Info, DateTime FetchedAt)> _galaxyScanCache = new();
		private const int MaxGalaxyScanCacheEntries = 1000;
		private TBotDataCache _harvestCache;

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
					// Permanent record for the AutoHarvest dashboard's "Gained (total)" panel -
					// requested live 2026-09-02, there was no results store wired for Harvest at all
					// before this. Recorded at dispatch time from the debris field's reported amount
					// (same convention AutoFarm's "attacks" table uses for loot), not a post-hoc
					// verified collection - OGame doesn't send a distinct "harvest complete" report
					// the way combat does.
					_harvestCache?.RecordHarvest(
						assignment.Target.Destination.Galaxy,
						assignment.Target.Destination.System,
						assignment.Target.Destination.Position,
						assignment.Target.Resources.Metal,
						assignment.Target.Resources.Crystal,
						assignment.Target.Resources.Deuterium);
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

		private async Task<GalaxyInfo> GetGalaxyInfoWithRetry(Coordinate coordinate) {
			int retryCount = 0;
			int maxRetries = 5;
			while (true) {
				try {
					return await _ogameService.GetGalaxyInfo(coordinate);
				} catch (Exception e) when (e.Message.Contains("system must be within") || e.Message.Contains("503") || e.Message.Contains("Service Unavailable")) {
					retryCount++;
					if (retryCount >= maxRetries) {
						DoLog(LogLevel.Warning, $"Galaxy scan for {coordinate} kept failing after {maxRetries} retries: {e.Message}. Giving up on this system.");
						throw;
					}
					int waitSeconds = retryCount * 3;
					DoLog(LogLevel.Warning, $"Galaxy scan failed for {coordinate}. Retry {retryCount}/{maxRetries} in {waitSeconds}s...");
					await Task.Delay(waitSeconds * 1000);
				}
			}
		}

		private async Task<GalaxyInfo> GetGalaxyInfoCached(Coordinate coordinate) {
			string key = $"{coordinate.Galaxy}:{coordinate.System}";
			TimeSpan maxAge = TimeSpan.FromMinutes((int) _tbotInstance.InstanceSettings.AutoHarvest.CheckIntervalMax);
			if (_galaxyScanCache.TryGetValue(key, out var cached)) {
				if (DateTime.UtcNow - cached.FetchedAt < maxAge) {
					return cached.Info;
				}
				_galaxyScanCache.Remove(key);
			}
			GalaxyInfo info = await GetGalaxyInfoWithRetry(coordinate);
			// Deliberately paced, not just a perf afterthought - scanning too fast previously triggered
			// real errors from the game server (confirmed by the user 2026-08-19). Default 1.5-2.5s is a
			// cautious step down from the shared IntervalType.LessThanFiveSeconds (1-5s) used elsewhere,
			// isolated to this scan via its own settings so tuning it doesn't affect other features'
			// pacing. Watch the log for 503/rate limit errors before lowering further.
			int delayMinMs = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoHarvest, "ScanDelayMinMs")
				? (int) _tbotInstance.InstanceSettings.AutoHarvest.ScanDelayMinMs
				: 1500;
			int delayMaxMs = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoHarvest, "ScanDelayMaxMs")
				? (int) _tbotInstance.InstanceSettings.AutoHarvest.ScanDelayMaxMs
				: 2500;
			await Task.Delay(new Random().Next(delayMinMs, delayMaxMs));
			if (_galaxyScanCache.Count >= MaxGalaxyScanCacheEntries) {
				foreach (var oldKey in _galaxyScanCache.OrderBy(kv => kv.Value.FetchedAt).Take(_galaxyScanCache.Count - MaxGalaxyScanCacheEntries + 1).Select(kv => kv.Key).ToList()) {
					_galaxyScanCache.Remove(oldKey);
				}
			}
			_galaxyScanCache[key] = (info, DateTime.UtcNow);
			_harvestCache?.MarkSystemScanned(coordinate.Galaxy, coordinate.System);
			return info;
		}

		// AutoHarvest.ScanRange: [{Galaxy, StartSystem, EndSystem}] - a wide network sweep for debris
		// fields beyond the player's own planets (gated by HarvestOthers, defaults to on if unset for
		// backward compat with instance files that only set ScanRange). Unlike own-DF/deep-space
		// above, treated with IsOwnDebris=true in HarvestPlanner terms (Recycler, not Pathfinder) -
		// these are real planet-position Debris fields, just not the player's own planet.
		private async Task<List<HarvestTarget>> DiscoverScanRangeTargets(List<Fleet> fleets) {
			var targets = new List<HarvestTarget>();
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoHarvest, "HarvestOthers") && !(bool) _tbotInstance.InstanceSettings.AutoHarvest.HarvestOthers)
				return targets;
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoHarvest, "ScanRange"))
				return targets;

			if (_harvestCache == null)
				_harvestCache = await TBotDataCache.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);

			var activeDestinations = (fleets ?? new List<Fleet>())
				.Where(fleet => fleet?.Mission == Missions.Harvest && fleet.Destination != null)
				.Select(fleet => fleet.Destination)
				.ToList();

			long minResources = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoHarvest, "MinimumResourcesOthers")
				? (long) _tbotInstance.InstanceSettings.AutoHarvest.MinimumResourcesOthers
				: (long) _tbotInstance.InstanceSettings.AutoHarvest.MinimumResourcesOwnDF;

			// The full network sweep (up to hundreds of systems, one request each) can exceed
			// Watchdog.MaxStuckMinutes and freeze the worker (confirmed 2026-08-19). Only re-run it
			// every ScanIntervalHours; every other cycle, pull candidates straight from
			// harvest_debris_history instead (cheap DB read) and only spend one live request per
			// candidate actually about to be sent to, to confirm the field is still there.
			double scanIntervalHours = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoHarvest, "ScanIntervalHours")
				? (double) _tbotInstance.InstanceSettings.AutoHarvest.ScanIntervalHours
				: 4.0;
			var lastFullScan = _harvestCache?.GetLastFullScanTime();
			bool fullScanDue = lastFullScan == null || (DateTime.UtcNow - lastFullScan.Value).TotalHours >= scanIntervalHours;

			if (!fullScanDue) {
				DoLog(LogLevel.Information, $"Skipping full ScanRange sweep (last one was {(DateTime.UtcNow - lastFullScan.Value):hh\\:mm\\:ss} ago, interval is {scanIntervalHours}h) - using cached debris positions instead.");
				// Cache retention: a sighting older than 3 full-scan intervals is stale enough that a
				// live check is basically free anyway (the field is as likely gone as not), so let the
				// next full scan rediscover it instead of trusting it here.
				var candidates = _harvestCache?.GetCachedDebrisCandidates(minResources, TimeSpan.FromHours(scanIntervalHours * 3)) ?? new();
				foreach (var (galaxy, system, position, metal, crystal) in candidates) {
					if (_ct.IsCancellationRequested)
						return targets;
					var dest = new Coordinate(galaxy, system, position, Celestials.Debris);
					if (ContainsCoordinate(activeDestinations, dest))
						continue;

					GalaxyInfo info;
					try {
						info = await GetGalaxyInfoCached(new Coordinate(galaxy, system, 1, Celestials.Planet));
					} catch {
						continue;
					}
					var planetInfo = info?.Planets?.FirstOrDefault(p => p != null && p.Coordinate.Position == position);
					if (planetInfo?.Debris == null || planetInfo.Debris.Resources.TotalResources < minResources)
						continue; // gone or depleted since last seen - skip, don't force a rescan just for this

					_harvestCache?.RecordDebrisSighting(galaxy, system, position, planetInfo.Debris.Resources.Metal, planetInfo.Debris.Resources.Crystal);
					if (!IsHarvestTargetExcluded(dest))
						targets.Add(new HarvestTarget(dest, planetInfo.Debris.Resources, true));
				}
				return targets;
			}

			foreach (var range in _tbotInstance.InstanceSettings.AutoHarvest.ScanRange) {
				int galaxy = (int) range.Galaxy;
				int startSystem = (int) range.StartSystem;
				int endSystem = (int) range.EndSystem;

				for (int system = startSystem; system <= endSystem; system++) {
					// A wide ScanRange can take longer than Watchdog.MaxStuckMinutes to finish one pass
					// (confirmed 2026-08-19 via a real "[FTL] Worker Harvest looks stuck" log entry) -
					// bail out cleanly on cancellation instead of letting Watchdog's restart just reset
					// the tracked start time while this loop keeps running in the background.
					if (_ct.IsCancellationRequested)
						return targets;
					GalaxyInfo info;
					try {
						info = await GetGalaxyInfoCached(new Coordinate(galaxy, system, 1, Celestials.Planet));
					} catch {
						continue;
					}
					if (info?.Planets == null)
						continue;

					foreach (var planetInfo in info.Planets) {
						if (planetInfo?.Debris == null || planetInfo.Debris.Resources.TotalResources < minResources)
							continue;

						_harvestCache?.RecordDebrisSighting(galaxy, system, planetInfo.Coordinate.Position, planetInfo.Debris.Resources.Metal, planetInfo.Debris.Resources.Crystal);

						var dest = new Coordinate(galaxy, system, planetInfo.Coordinate.Position, Celestials.Debris);
						if (ContainsCoordinate(activeDestinations, dest))
							continue;
						if (!IsHarvestTargetExcluded(dest))
							targets.Add(new HarvestTarget(dest, planetInfo.Debris.Resources, true));
					}
				}
			}
			_harvestCache?.SetLastFullScanTime(DateTime.UtcNow);
			return targets;
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
					var info = await GetGalaxyInfoCached(new Coordinate(system.Galaxy, system.System, 1, Celestials.Planet));
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

			targets.AddRange(await DiscoverScanRangeTargets(fleets));

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
					(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize,
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

		private bool IsHarvestTargetExcluded(Coordinate dest) {
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoHarvest, "Exclude"))
				return false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoHarvest.Exclude) {
				if ((int) exclude.Galaxy == dest.Galaxy && (int) exclude.System == dest.System && (int) exclude.Position == dest.Position)
					return true;
			}
			return false;
		}
	}
}
