using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Common.Logging;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using Tbot.Services;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using TBot.Model;
using TBot.Ogame.Infrastructure.Models;
using TBot.Ogame.Infrastructure;
using Tbot.Common.Settings;

namespace Tbot.Workers {
	public class AutoFarmWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		private readonly AutoFarmBlacklist _blacklist;
		private readonly AutoFarmSuccessfulTargets _successfulTargets;
		private readonly AutoFarmStateStore _stateStore;
		private bool _stateLoaded;

		// Fleet IDs of dispatched attacks still waiting on a real combat report, keyed by the
		// destination coordinate they were sent to. AutoFarm never reads the real post-battle
		// result back today - it only simulates the battle before sending (TryGetAcceptableCombatFleet)
		// and marks the target AttackSent once dispatched. This resolves each pending fleet ID
		// directly via GetCombatReportSummaryForFleet (a targeted per-fleet lookup, not scanning
		// the account's whole message list) once the report becomes available server-side.
		private readonly Dictionary<int, Coordinate> _pendingCombatReports = new();

		// Structured, WebUI-facing counterpart to LogResolvedCombatReports' worker-log line -
		// without this, a resolved combat report only ever existed as one line buried in the
		// scrolling worker log (confirmed live 2026-08-29: user reported "não detecta log de farm",
		// i.e. nothing surfaced on the AutoFarm dashboard itself). Same store/pattern already used
		// by ExpeditionsWorker for expedition/discovery results.
		private FeatureResultsStore _farmResultsStore;

		private async Task LogResolvedCombatReports(bool suppressLogging = false) {
			if (_pendingCombatReports.Count == 0)
				return;

			foreach (var fleetId in _pendingCombatReports.Keys.ToList()) {
				CombatReportSummary report;
				try {
					report = await _ogameService.GetCombatReportSummaryForFleet(fleetId);
				} catch {
					continue;
				}

				if (report == null || report.ID <= 0)
					continue;

				var target = _pendingCombatReports[fleetId];

				bool probeLost = report.LostShipsAttacker != null &&
					report.LostShipsAttacker.ContainsKey("EspionageProbe") &&
					report.LostShipsAttacker["EspionageProbe"] > 0;

				if (probeLost) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
						$"Combat report for {target}: EspionageProbe lost - blacklisting target.");
					if (_farmTargetCache != null) {
						int resetHours = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 168);
						_farmTargetCache.Blacklist(target, DateTime.UtcNow.AddHours(resetHours));
					}
					var farmTarget = _tbotInstance.UserData.farmTargets.FirstOrDefault(t => t.Celestial.Coordinate.Equals(target));
					if (farmTarget != null) {
						farmTarget.State = FarmState.NotSuitable;
					}
					_pendingCombatReports.Remove(fleetId);
					continue;
				}

				if (!suppressLogging)
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
						$"Combat report for attack on {target}: looted {report.Loot} " +
						$"({report.Metal}M/{report.Crystal}C/{report.Deuterium}D), debris field {report.DebrisField}.");
				_farmResultsStore.Add(new FeatureResultEntry {
					Id = report.ID,
					Coordinate = target.ToString(),
					Summary = $"Looted {report.Loot} ({report.Metal}M/{report.Crystal}C/{report.Deuterium}D), debris field {report.DebrisField}.",
					Resources = report.Loot > 0 ? $"{report.Metal}M/{report.Crystal}C/{report.Deuterium}D" : "",
					Ships = "",
					TimestampUtc = report.CreatedAt,
					Metal = report.Metal,
					Crystal = report.Crystal,
					Deuterium = report.Deuterium
				});
				_pendingCombatReports.Remove(fleetId);
			}
		}

		// FarmTargetCache (SQLite) - THE single source of truth for FastFarm AND ScanOnly data.
		// Stores persistent target cache, blacklist, attack history, AND system scan cache.
		// Inactive status is ETERNAL once marked (InactiveStatusLocked = true) - never auto-cleared.
		// Inactivity is determined by UNIVERSE VIEW (galaxy scans), NOT /api/players.xml.
		private FarmTargetCache _farmTargetCache;

		// Universe View Inactivity Checker - replaces PlayerStatusCache (/api/players.xml).
		// Authoritative inactivity detection via galaxy scans (universe view).
		private UniverseViewInactivityChecker _universeViewChecker;

		// Free byproduct of the galaxy scans AutoFarm already does for target vetting - shares
		// sightings into AutoHarvest's ScanRange history (harvest_debris_history) instead of
		// throwing away the per-position debris data GetGalaxyInfo returns anyway. Restored
		// 2026-08-24, see HarvestWorker.cs's TBotDataCache restoration note for context.
		private TBotDataCache _harvestCache;

		// Lazily created once and reused across Execute() cycles (unlike _farmTargetCache) since it
		// only holds an in-memory, self-refreshing snapshot of /api/highscore.xml - no per-cycle
		// open/dispose needed.
		private HighscorePlayerRankCache _highscoreRankCache;

		// Legacy PlayerStatusCache - KEPT for backward compatibility but NO LONGER USED for inactivity detection.
		// Inactivity is now determined exclusively via UniverseViewInactivityChecker (galaxy scans).
		// This cache is retained only for potential future use or debugging.
		private PlayerStatusCache _playerStatusCache;

		// Tracks the single expendable probe-as-attack sent per target to detect defenses when a
		// normal espionage report couldn't reveal them (see SendDefenseProbeAttacks). Static so it
		// survives worker re-instantiation, matching the cross-cycle nature of "wait for this fleet
		// to arrive, then wait for it to either return or not."
		private record DefenseProbeInfo(int FleetId, DateTime SentAt, DateTime? ArrivalTime, bool SeenReturning);
		private static readonly Dictionary<string, DefenseProbeInfo> _defenseProbeFleets = new();

		public AutoFarmWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
			string blacklistPath = $"autofarm_blacklist_{_tbotInstance.InstanceAlias}.json";
			_blacklist = new AutoFarmBlacklist(blacklistPath);
			_farmResultsStore = new FeatureResultsStore($"farm_results_{_tbotInstance.InstanceAlias}.json");
			string successfulPath = $"autofarm_successful_{_tbotInstance.InstanceAlias}.json";
			_successfulTargets = new AutoFarmSuccessfulTargets(successfulPath);
			_stateStore = new AutoFarmStateStore($"autofarm_{_tbotInstance.InstanceAlias}.db");
		}
		// Reverted 2026-09-02 per explicit user request: Active:false now stops this worker
		// completely again (no background attack-dispatching/log noise), same as every other worker.
		// Combat-report resolution moved to OnDisabledTick so already-dispatched attacks still get
		// their loot recorded in the results store while Active is off.
		public override bool IsWorkerEnabledBySettings() => (bool) _tbotInstance.InstanceSettings.AutoFarm.Active;
		public override string GetWorkerName() {
			return "AutoFarm";
		}
		public override Feature GetFeature() {
			return Feature.AutoFarm;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoFarm;
		}

		protected override async Task OnDisabledTick() {
			// Even when disabled, keep resolving combat reports for previously dispatched attacks
			// so loot gets recorded in the results store.
			await LogResolvedCombatReports(suppressLogging: true);
		}

		private void LoadPersistedTargets() {
			if (_stateLoaded)
				return;

			_tbotInstance.UserData.farmTargets ??= new List<FarmTarget>();
			foreach (var persisted in _stateStore.LoadTargets()) {
				if (!persisted.ConsumedReportId.HasValue &&
					persisted.State == FarmState.AttackSent &&
					persisted.Report?.ID > 0) {
					// Databases created before report consumption was persisted still
					// contain the report that was used for this completed dispatch.
					persisted.ConsumedReportId = persisted.Report.ID;
				}

				var current = _tbotInstance.UserData.farmTargets.FirstOrDefault(target =>
					target?.Celestial?.Coordinate?.IsSame(persisted.Celestial.Coordinate) == true);
				if (current == null) {
					_tbotInstance.UserData.farmTargets.Add(persisted);
					continue;
				}

				current.Celestial = persisted.Celestial;
				current.State = persisted.State;
				current.Report = persisted.Report;
				current.ConsumedReportId = persisted.ConsumedReportId;
			}

			// Filter out targets whose player is marked as active (prevents attacking active players on restart)
			if (_farmTargetCache != null) {
				var activePlayerIds = _farmTargetCache.GetActivePlayerIds();
				if (activePlayerIds.Count > 0) {
					var removed = _tbotInstance.UserData.farmTargets
						.Where(t => t?.Celestial is Planet p && p.Player?.ID > 0 && activePlayerIds.Contains(p.Player.ID))
						.ToList();
					foreach (var r in removed) {
						var playerName = r.Celestial is Planet pl ? pl.Player?.Name : "unknown";
						var playerId = r.Celestial is Planet pl2 ? pl2.Player?.ID ?? 0 : 0;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"LoadPersistedTargets: Removed target {r.Celestial.Coordinate} - player {playerName} (ID:{playerId}) is ACTIVE per universe view");
					}
					_tbotInstance.UserData.farmTargets.RemoveAll(t => t?.Celestial is Planet p && p.Player?.ID > 0 && activePlayerIds.Contains(p.Player.ID));
				}
			}

			var cursor = _stateStore.LoadScanCursor();
			if (cursor != null) {
				_tbotInstance.UserData.autoFarmLastRangeIndex = cursor.RangeIndex;
				_tbotInstance.UserData.autoFarmLastGalaxy = cursor.Galaxy;
				_tbotInstance.UserData.autoFarmLastSystem = cursor.System;
				_tbotInstance.UserData.autoFarmFullScanCompleted = cursor.FullScanCompleted;
			}
			_stateLoaded = true;
		}

		private void PersistTargets() {
			_stateStore.ReplaceTargets(_tbotInstance.UserData.farmTargets, DateTime.UtcNow);
		}

		private void SaveScanCursor(int rangeIndex, int galaxy, int system) {
			_tbotInstance.UserData.autoFarmLastRangeIndex = rangeIndex;
			_tbotInstance.UserData.autoFarmLastGalaxy = galaxy;
			_tbotInstance.UserData.autoFarmLastSystem = system;
			_stateStore.SaveScanCursor(rangeIndex, galaxy, system, DateTime.UtcNow);
		}

		private TimeSpan GetSystemDataTtl() {
			int days = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm,
				"DaysToKeepOldSystemData", 7);
			return TimeSpan.FromDays(Math.Max(1, days));
		}

		private TimeSpan GetEmptySystemCooldown() {
			int days = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm,
				"EmptySystemCooldownDays", 30);
			return TimeSpan.FromDays(Math.Max(1, days));
		}

		/// <summary>
		/// Distinction between KeepReportFor and FastFarmReportRetentionDays:
		/// - KeepReportFor (AutoFarm-specific): How long to keep an espionage report before considering it stale
		///   and re-probing the target. Applies only to targets in AttackPending/ProbesPending states that have
		///   an associated EspionageReport. Controls the autofarm report lifecycle.
		/// - FastFarmReportRetentionDays (Cache management): How long to keep FarmTargetCache entries that
		///   were ONLY seen in galaxy scans (no espionage report). Applies to ScanOnly, FastFarm, and AutoFarm
		///   modes equally. Controls the persistent cache cleanup for scan-only entries.
		/// </summary>
		private async Task PruneOldReports(List<Fleet> fleets) {
			var newTime = await _tbotOgameBridge.GetDateTime();
			var removeReports = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent || (t.Report != null && DateTime.Compare(t.Report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), newTime) < 0)).ToList();
			foreach (var remove in removeReports) {
				if (remove.State == FarmState.AttackSent && AutoFarmAttackPolicy.IsAttackInProgress(remove, fleets)) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Keeping {remove} in AttackSent state while an attack fleet is still in progress.");
					continue;
				}

				var updateReport = remove;
				updateReport.State = FarmState.ProbesPending;
				updateReport.Report = null;
				_tbotInstance.UserData.farmTargets.Remove(remove);
				_tbotInstance.UserData.farmTargets.Add(updateReport);
			}
		}

		private async Task ProcessProbeRetries(List<Celestial> farmOrigins, Dictionary<int, long> celestialProbes, int slotsToLeaveFree, int freeSlots) {
			var probeRetrySettings = _tbotInstance.InstanceSettings.AutoFarm.ProbeRetry;
			if (probeRetrySettings == null)
				return;

			int maxRetries = SettingsService.GetSetting(probeRetrySettings, "MaxRetries", 3);
			int retryDelaySeconds = SettingsService.GetSetting(probeRetrySettings, "RetryDelaySeconds", 300);
			int blacklistAfterFailures = SettingsService.GetSetting(probeRetrySettings, "BlacklistAfterFailures", 5);
			int blacklistHours = SettingsService.GetSetting(probeRetrySettings, "BlacklistHours", 24);

			var now = await _tbotOgameBridge.GetDateTime();
			var retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);

			var targetsToRetry = _tbotInstance.UserData.farmTargets
				.Where(t => t.State == FarmState.ProbesSent && t.LastProbeSentAt.HasValue && (now - t.LastProbeSentAt.Value) >= retryDelay)
				.ToList();

			foreach (var target in targetsToRetry) {
				if (freeSlots <= slotsToLeaveFree) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "No free slots for probe retry.");
					break;
				}

				if (target.ProbeRetryCount >= maxRetries) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {target} reached max probe retries ({maxRetries}), blacklisting for {blacklistHours}h.");
					_blacklist.AddTarget(target.Celestial.Coordinate, BlacklistReason.ProbeFailure, blacklistHours);
					_farmTargetCache?.Blacklist(target.Celestial.Coordinate, DateTime.UtcNow.AddHours(blacklistHours));
					string playerName = target.Report?.Username;
					if (string.IsNullOrEmpty(playerName) && target.Celestial is Planet planet)
						playerName = planet.Player?.Name;
					if (!string.IsNullOrEmpty(playerName)) {
						_blacklist.AddPlayer(playerName, BlacklistReason.ProbeFailure, blacklistHours);
						_farmTargetCache?.BlacklistPlayer(playerName, DateTime.UtcNow.AddHours(blacklistHours));
					}
					
					var blacklistedTarget = target;
					blacklistedTarget.State = FarmState.NotSuitable;
					_tbotInstance.UserData.farmTargets.Remove(target);
					_tbotInstance.UserData.farmTargets.Add(blacklistedTarget);
					continue;
				}

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Retrying probe on {target} (attempt {target.ProbeRetryCount + 1}/{maxRetries}).");

				var closestCelestials = farmOrigins
					.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData)).ToList();

				if (!closestCelestials.Any()) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"No origin celestials available for retry on {target}.");
					continue;
				}

				int neededProbes = GetNeededProbes(target);

				var bestOrigin = await GetBestOrigin(closestCelestials, celestialProbes, target, neededProbes, slotsToLeaveFree, freeSlots);
				freeSlots = bestOrigin.FreeSlots;

				if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
					var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
					celestialProbes.Remove(bestOrigin.Origin.ID);
					celestialProbes.Add(bestOrigin.Origin.ID, tempCelestial.Ships.EspionageProbe);
				}

				if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Insufficient probes for retry on {target} ({celestialProbes[bestOrigin.Origin.ID]}/{neededProbes}).");
					continue;
				}

				if (_calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets).Any(f => f.Destination.IsSame(target.Celestial.Coordinate))) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Probes already on route towards {target} (retry skipped).");
					continue;
				}

				Ships ships = new();
				ships.Add(Buildables.EspionageProbe, neededProbes);

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Retrying spy on {target} from {bestOrigin.Origin} with {neededProbes} probes.");

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				var slotBudget = GetCurrentSlotBudget(GetConfiguredCargoType());
				if (slotBudget.AvailableSlots <= 0) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "AutoFarm slot budget exhausted; no probe retry dispatched.");
					break;
				}

				bestOrigin.Origin = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
				var availableProbes = bestOrigin.Origin.Ships.EspionageProbe;

				if (availableProbes < neededProbes) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Insufficient probes on {bestOrigin.Origin} ({availableProbes}/{neededProbes}). Skipping retry for {target}.");
					continue;
				}

				var fleetId = await _fleetScheduler.SendFleet(bestOrigin.Origin, ships, target.Celestial.Coordinate, Missions.Spy, Speeds.HundredPercent, null, _tbotInstance.UserData.userInfo.Class, false, true);

				if (fleetId > (int) SendFleetCode.GenericError) {
					freeSlots--;
					celestialProbes[bestOrigin.Origin.ID] -= neededProbes;

					var updatedTarget = target;
					updatedTarget.ProbeRetryCount++;
					updatedTarget.LastProbeSentAt = now;
					_tbotInstance.UserData.farmTargets.Remove(target);
					_tbotInstance.UserData.farmTargets.Add(updatedTarget);

					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Probe retry dispatched for {target} (fleet ID: {fleetId}, retry count: {updatedTarget.ProbeRetryCount}).");
				} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
					break;
				} else if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Another worker took the last available slot; pausing probe retries.");
					break;
				}
			}
		}

		private Dictionary<int, long> GetCachedCelestialProbes(List<Celestial> origins) {
			return (origins ?? new List<Celestial>())
				.Where(celestial => celestial != null)
				.GroupBy(celestial => celestial.ID)
				.ToDictionary(
					group => group.Key,
					group => group.First().Ships?.EspionageProbe ?? 0);
		}

		// Live variant kept from the previous tree: refreshes every celestial before reading its
		// probe count, for the code paths that cannot rely on an already-updated origin list.
		private async Task<Dictionary<int, long>> GetCelestialProbes() {
			var localCelestials = await _tbotOgameBridge.UpdateCelestials();
			Dictionary<int, long> celestialProbes = new Dictionary<int, long>();
			foreach (var celestial in localCelestials) {
				Celestial tempCelestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);
				tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
				celestialProbes.Add(tempCelestial.ID, tempCelestial.Ships.EspionageProbe);
			}
			return celestialProbes;
		}

		private List<Celestial> GetFarmOrigins() {
			List<Celestial> origins;
			if (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0)
				origins = _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials);
			else
				origins = _tbotInstance.UserData.celestials;

			return (origins ?? new List<Celestial>())
				.Where(celestial => celestial?.Coordinate != null)
				.GroupBy(celestial => $"{celestial.Coordinate.Galaxy}:{celestial.Coordinate.System}:{celestial.Coordinate.Position}:{celestial.Coordinate.Type}")
				.Select(group => group.First())
				.ToList();
		}

		private long GetMinDistanceFromOrigins(Coordinate targetCoord, List<Celestial> origins) {
			if (targetCoord == null || origins == null || origins.Count == 0)
				return long.MaxValue;
			long minDist = long.MaxValue;
			foreach (var origin in origins) {
				if (origin.Coordinate == null)
					continue;
				long dist = _calculationService.CalcDistance(origin.Coordinate, targetCoord, _tbotInstance.UserData.serverData);
				if (dist < minDist)
					minDist = dist;
			}
			return minDist;
		}

		private long GetDistance(Coordinate from, Coordinate to) {
			if (from == null || to == null || _tbotInstance.UserData.serverData == null)
				return long.MaxValue;
			return _calculationService.CalcDistance(from, to, _tbotInstance.UserData.serverData);
		}

		private bool ShouldExcludeSystem(int galaxy, int system) {
			bool excludeSystem = false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoFarm.Exclude) {
				bool hasPosition = false;
				foreach (var value in exclude.Keys)
					if (value == "Position")
						hasPosition = true;
				if ((int) exclude.Galaxy == galaxy && (int) exclude.System == system && !hasPosition) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping system {system.ToString()}: system in exclude list.");
					excludeSystem = true;
					break;
				}
			}
			return excludeSystem;
		}

		// Persists what a live scan found for this celestial into the FastFarm cache - basic identity
		// only (coordinate, player, inactive flag), so a later FastFarmMode cycle can rebuild the
		// system's target list without a live galaxy scan. Report-derived detail (resources,
		// buildings, defenses) is filled in separately by CacheUpsertFromReport once actually probed.
		private async Task CacheUpsertFromScan(Celestial celestial) {
			var planet = celestial as Planet;
			if (planet == null)
				return;
			var entry = _farmTargetCache.Get(planet.Coordinate) ?? new FarmTargetCacheEntry { Coordinate = planet.Coordinate };
			entry.PlayerId = planet.Player?.ID ?? entry.PlayerId;
			entry.PlayerName = planet.Player?.Name;
			entry.PlayerRank = planet.Player?.Rank ?? entry.PlayerRank;
			entry.IsAdministrator = planet.Administrator;
			entry.IsBanned = planet.Banned;
			entry.IsVacation = planet.Vacation;
			entry.LastSeenDate = DateTime.UtcNow;

			// Universe View Inactivity Detection (replaces /api/players.xml)
			// Inactive status is ETERNAL once locked (InactiveStatusLocked = true) - never auto-cleared.
			// Source is "universe_view" (galaxy scan).
			if (planet.Inactive) {
				if (!entry.IsInactive || !entry.InactiveStatusLocked) {
					entry.IsInactive = true;
					entry.InactivitySource = "universe_view";
					entry.InactiveSince ??= DateTime.UtcNow;
					entry.InactiveStatusLocked = true; // ETERNAL persistence
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
						$"CacheUpsertFromScan: {planet.Coordinate} marked INACTIVE (eternal) via universe view");
				}
				// Player-level: mark player as potentially inactive
				if (entry.PlayerId > 0 && !string.IsNullOrEmpty(entry.PlayerName)) {
					_farmTargetCache.SetPlayerActivity(entry.PlayerId, entry.PlayerName, false, null);
				}
			} else {
				// Planet is not inactive in universe view - mark PLAYER as ACTIVE immediately
				// This prevents wasting fleets/probes on ANY of this player's coordinates
				if (entry.PlayerId > 0 && !string.IsNullOrEmpty(entry.PlayerName)) {
					_farmTargetCache.SetPlayerActivity(entry.PlayerId, entry.PlayerName, true, null);
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
						$"CacheUpsertFromScan: PLAYER {entry.PlayerName} (ID:{entry.PlayerId}) marked ACTIVE via universe view. All coordinates for this player will be skipped.");
				}
				// Planet-level: ONLY update if not already locked as inactive (eternal persistence)
				if (!entry.InactiveStatusLocked) {
					entry.IsInactive = false;
					entry.InactivitySource = "universe_view";
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"CacheUpsertFromScan: {planet.Coordinate} marked ACTIVE via universe view");
				} else {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"CacheUpsertFromScan: {planet.Coordinate} remains INACTIVE (locked) despite universe view showing active - eternal persistence");
				}
			}

			await _farmTargetCache.Upsert(entry);
		}

		// Free byproduct of a galaxy scan we're already doing for target vetting - GetGalaxyInfo
		// returns per-position debris data whether we look at it or not, so record it into
		// AutoHarvest's shared history instead of throwing it away.
		private void RecordDebrisFromScan(GalaxyInfo galaxyInfo) {
			if (_harvestCache == null || galaxyInfo?.Planets == null)
				return;
			foreach (var planetInfo in galaxyInfo.Planets) {
				if (planetInfo?.Debris == null || planetInfo.Debris.Resources.TotalResources <= 0)
					continue;
				_harvestCache.RecordDebrisSighting(galaxyInfo.Galaxy, galaxyInfo.System, planetInfo.Coordinate.Position, planetInfo.Debris.Resources.Metal, planetInfo.Debris.Resources.Crystal);
			}
		}

		private async Task<List<Celestial>> GetScannedTargetsFromGalaxy(int galaxy, int system) {
			DateTime nowUtc = DateTime.UtcNow;

			// FastFarmMode: when explicitly enabled, a recent enough cached scan of this system
			// replaces the live galaxy scan entirely. Renamed from FastFarmMaxCacheAge (minutes) to
			// FastFarmMaxCacheAgeDays - requested live 2026-09-05, a 30-day cache window is far more
			// readable as "30" days than "43200" minutes. Kept as a distinct key rather than
			// reinterpreting the old one in place, so an existing minutes-based value in someone's
			// settings file can't silently be misread as a (wildly different) days value.
			bool fastFarmMode = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmMode") && (bool) _tbotInstance.InstanceSettings.AutoFarm.FastFarmMode;
			double fastFarmMaxCacheAgeDays = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmMaxCacheAgeDays") ? (double) _tbotInstance.InstanceSettings.AutoFarm.FastFarmMaxCacheAgeDays : 1;

			if (fastFarmMode && _farmTargetCache != null) {
				var scanAge = _farmTargetCache.GetSystemScanAge(galaxy, system);
				if (scanAge != null && scanAge.Value.TotalDays < fastFarmMaxCacheAgeDays) {
					var cachedEntries = _farmTargetCache.GetInRange(galaxy, system, system)
						.Where(e => e.IsInactive && e.InactiveStatusLocked && !e.IsAdministrator && !e.IsBanned && !e.IsVacation)
						.ToList();
					// Filter out targets whose player is marked as active (player-level activity tracking)
					var activePlayerIds = _farmTargetCache.GetActivePlayerIds();
					if (activePlayerIds.Count > 0) {
						var beforeCount = cachedEntries.Count;
						cachedEntries = cachedEntries.Where(e => !activePlayerIds.Contains(e.PlayerId)).ToList();
						if (cachedEntries.Count < beforeCount) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, 
								$"FastFarm: filtered out {beforeCount - cachedEntries.Count} targets in {galaxy}:{system} because their player is marked ACTIVE");
						}
					}
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"FastFarm: reusing cached scan of {galaxy}:{system} ({cachedEntries.Count} target(s), scanned {scanAge.Value.TotalDays:F1}d ago) instead of a live galaxy scan.");
					List<Celestial> cachedTargets = cachedEntries.Select(e => (Celestial) new Planet {
						Coordinate = e.Coordinate,
						Inactive = e.IsInactive,
						Administrator = e.IsAdministrator,
						Banned = e.IsBanned,
						Vacation = e.IsVacation,
						Player = new Player { ID = e.PlayerId, Name = e.PlayerName, Rank = e.PlayerRank }
					}).ToList();
					await _fleetScheduler.UpdateFleets();
					cachedTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));
					// Universe View Inactivity Check: Use FarmTargetCache's eternal inactive status (InactiveStatusLocked)
					// instead of /api/players.xml. Inactive status is now ETERNAL once marked via universe view.
					var stillInactive = new List<Celestial>();
					foreach (var t in cachedTargets) {
						var coord = t.Coordinate;
						var isInactive = await _universeViewChecker.IsInactiveViaUniverseView(coord, (t as Planet)?.Player?.ID);
						if (isInactive == true) {
							stillInactive.Add(t);
						} else if (isInactive == false) {
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"FastFarm: {t} is ACTIVE per universe view - dropping cached target.");
						} else {
							// Unknown - be conservative, keep the target (eternal persistence means we trust the DB)
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"FastFarm: {t} status UNKNOWN via universe view - keeping cached target (eternal persistence).");
							stillInactive.Add(t);
						}
					}
					return stillInactive;
				}
			}

			// Under ScanOnly, always honor the stored cache using the existing DaysToKeepOldSystemData
			// setting (reused rather than adding a new dedicated key - requested live 2026-09-05: "vou
			// usar DaysToKeepOldSystemData: 60, para configurar ScanOnly também") for BOTH "system has
			// planets" and "system is empty" decisions - ScanOnly has no attack-safety reason to force
			// a live re-scan of an already-fresh system (see the two guards below), unlike the normal
			// attack flow, which still needs the full-sweep bypass for freshness guarantees.
			bool scanOnlyForCache = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ScanOnly")
				&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ScanOnly;
			TimeSpan effectiveDataTtl = GetSystemDataTtl();
			TimeSpan effectiveEmptyCooldown = scanOnlyForCache ? effectiveDataTtl : GetEmptySystemCooldown();

			var cachedSnapshot = _stateStore.GetSystemSnapshot(galaxy, system);
			var cacheDecision = AutoFarmSystemCachePolicy.GetDecision(
				cachedSnapshot,
				nowUtc,
				effectiveDataTtl,
				effectiveEmptyCooldown);
			// The empty-system cooldown (EmptySystemCooldownDays, 30 here) means a system found
			// empty once gets skipped for up to a month - directly contradicts "search every
			// inactive in the configured range" during the FIRST sweep of a freshly widened
			// ScanRange (requested live 2026-09-02: user expanded to G1-G4 and wants every system
			// actually checked, not silently excluded because of a stale/unrelated earlier scan).
			// Only honor the skip once that first full sweep has completed - subsequent cycles keep
			// the cooldown as the intended performance optimization. Under ScanOnly, always honor it
			// (using DaysToKeepOldSystemData instead of EmptySystemCooldownDays, see above) - there is
			// no attack to protect against stale data, so forcing a live re-scan of an already-fresh
			// system is just wasted probes.
			if (cacheDecision == AutoFarmSystemCacheDecision.Skip && (scanOnlyForCache || _tbotInstance.UserData.autoFarmFullScanCompleted)) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Skipping cached empty system {galaxy}:{system} until {cachedSnapshot.ObservedAtUtc.Add(effectiveEmptyCooldown):u}.");
				return null;
			}

			// Same reasoning as the Skip guard above: reusing up-to-30-day-old planet data
			// (DaysToKeepOldSystemData) could hide a player who went inactive since that snapshot
			// was taken - force a live scan for every system until the first full sweep completes.
			// Under ScanOnly, always honor it instead (see above).
			if (cacheDecision == AutoFarmSystemCacheDecision.UseSnapshot && (scanOnlyForCache || _tbotInstance.UserData.autoFarmFullScanCompleted)) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Using cached galaxy data for {galaxy}:{system} ({(nowUtc - cachedSnapshot.ObservedAtUtc).TotalDays:F1} days old).");
				var snapshotTargets = AutoFarmSystemPolicy.GetEligibleTargets(cachedSnapshot.Planets);
				// Same guard as the live-scan path: never hand back a coordinate that already has an
				// attack fleet on its way.
				await _fleetScheduler.UpdateFleets();
				snapshotTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));

				// This path (reusing AutoFarmStateStore's older snapshot) was the one actually taken
				// once a full sweep had completed - which meant FarmTargetCache (FastFarmMode's own
				// cache, fed only from the live-scan branch below) never got populated at all for an
				// instance that already had a mature scan history. FastFarmMode's per-system reordering
				// by known profitability silently had nothing to work with. Feed it here too.
				if (_farmTargetCache != null) {
					foreach (var target in snapshotTargets) {
						await CacheUpsertFromScan(target);
					}
					_farmTargetCache.MarkSystemScanned(galaxy, system);
				}

				// Same staleness concern as the FastFarmMode cache branch above: this snapshot can be up
				// to DaysToKeepOldSystemData old, so re-verify against the universe view (FarmTargetCache)
				// before treating anyone in it as still inactive. Inactive status is ETERNAL once locked.
				var stillInactiveSnapshot = new List<Celestial>();
				foreach (var t in snapshotTargets) {
					var coord = t.Coordinate;
					var isInactive = await _universeViewChecker.IsInactiveViaUniverseView(coord, (t as Planet)?.Player?.ID);
					if (isInactive == true) {
						stillInactiveSnapshot.Add(t);
					} else if (isInactive == false) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"{t} is ACTIVE per universe view - dropping cached target.");
					} else {
						// Unknown - be conservative, keep the target (eternal persistence means we trust the DB)
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"{t} status UNKNOWN via universe view - keeping cached target (eternal persistence).");
						stillInactiveSnapshot.Add(t);
					}
				}
				return stillInactiveSnapshot;
			}

			GalaxyInfo galaxyInfo = null;
			int retryCount = 0;
			int maxRetries = 5;

			while (retryCount < maxRetries) {
				try {
					galaxyInfo = await _ogameService.GetGalaxyInfo(galaxy, system);
					RecordDebrisFromScan(galaxyInfo);
					break;
				} catch (Exception e) when (e.Message.Contains("system must be within") || e.Message.Contains("503") || e.Message.Contains("Service Unavailable")) {
					retryCount++;
					int waitSeconds = retryCount * 3;

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Exception details: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Galaxy scan failed. Retry {retryCount}/{maxRetries} in {waitSeconds}s...");

					if (retryCount < maxRetries) {
						await Task.Delay(waitSeconds * 1000);

						try {
							_tbotInstance.UserData.serverData = await _ogameService.GetServerData();
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"ServerData after refresh: Systems={_tbotInstance.UserData.serverData.Systems}");
						} catch (Exception serverDataEx) {
							_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to refresh ServerData: {serverDataEx.Message}");
						}

						if (retryCount == 4 && _tbotInstance.UserData.serverData.Systems == 0) {
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"ServerData broken. Root cause: {e.Message}. Restarting ogamed...");

							try {
								_ogameService.KillOgamedExecutable();
								await Task.Delay(5000);
								_ogameService.RerunOgamed();
								await Task.Delay(10000);

								try {
									_tbotInstance.UserData.serverData = await _ogameService.GetServerData();
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"ServerData after restart: Systems={_tbotInstance.UserData.serverData.Systems}");
								} catch (Exception restartEx) {
									_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to get ServerData after ogamed restart: {restartEx.Message}");
								}
							} catch (Exception ogamedEx) {
								_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to restart ogamed: {ogamedEx.Message}");
								_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {ogamedEx.StackTrace}");
							}
						}
					} else {
						_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Galaxy scan failed after {maxRetries} retries.");
						throw;
					}
				}
			}

			if (galaxyInfo?.Planets == null) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Galaxy scan returned no planet data for {galaxy}:{system}; this system will not be cached as empty.");
				return null;
			}

			var planets = galaxyInfo.Planets.Where(p => p != null).ToList();
			bool isEmptySystem = AutoFarmSystemPolicy.IsEmptySystem(planets);
			_stateStore.SaveSystemSnapshot(galaxy, system, nowUtc, isEmptySystem, planets);

			if (isEmptySystem)
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"System {galaxy}:{system} is empty for AutoFarm; applying the configured long cooldown.");

			List<Celestial> scannedTargets = AutoFarmSystemPolicy.GetEligibleTargets(planets);
			await _fleetScheduler.UpdateFleets();
			scannedTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));

			// Feed the FastFarm cache with what this live scan found, regardless of whether
			// FastFarmMode is on right now - a later cycle with it enabled reuses this.
			if (_farmTargetCache != null) {
				foreach (var target in scannedTargets) {
					await CacheUpsertFromScan(target);
				}
				_farmTargetCache.MarkSystemScanned(galaxy, system);
			}

			return scannedTargets;
		}

		private async Task<bool> IsTargetInMinimumRank(Celestial planet, List<Celestial> scannedTargets) {
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinimumPlayerRank") && _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank != 0) {
				int rank = 1;
				if (planet.Coordinate.Type == Celestials.Planet) {
					rank = (planet as Planet).Player.Rank;
				} else if (scannedTargets.Any(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet)))) {
					rank = (scannedTargets.Single(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet))) as Planet).Player.Rank;
				} else {
					// Sibling planet wasn't in this scan (e.g. a moon whose planet is out of range or
					// already filtered out). Fall back to the persistent target cache's last known
					// player name, then resolve a fresh rank for it via highscore.xml instead of
					// silently defaulting to rank 1 (which always passes the filter).
					var cachedEntry = _farmTargetCache?.Get(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet));
					if (!string.IsNullOrEmpty(cachedEntry?.PlayerName)) {
						_highscoreRankCache ??= new HighscorePlayerRankCache(_ogameService);
						rank = await _highscoreRankCache.GetRank(cachedEntry.PlayerName) ?? cachedEntry.PlayerRank;
					}
				}
				if ((int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank < rank) {

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Skipping {planet.ToString()}: player has rank {rank} that is greater than maximum configured {(int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank}.");
					return false;
				}
			}
			return true;
		}

		private bool ShouldExcludePlanet(Celestial planet) {
			bool excludePlanet = false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoFarm.Exclude) {
				bool hasPosition = false;
				foreach (var value in exclude.Keys)
					if (value == "Position")
						hasPosition = true;
				if ((int) exclude.Galaxy == planet.Coordinate.Galaxy && (int) exclude.System == planet.Coordinate.System && hasPosition && (int) exclude.Position == planet.Coordinate.Position) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {planet.ToString()}: celestial in exclude list.");
					excludePlanet = true;
					break;
				}
			}
			return excludePlanet;
		}

		private bool IsDefenseProbeEnabled() =>
			SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProbeAttackForDefenseCheck")
			&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ProbeAttackForDefenseCheck;

		// Some targets never reveal defenses through espionage (insufficient Espionage Technology on
		// the report, or it's simply never come back with HasDefensesInformation). Instead of leaving
		// them stuck as ProbesRequired forever, sends a single expendable Espionage Probe as an ATTACK
		// mission - if it survives and comes home, the target has no defenses (queue the real attack);
		// if it never returns, the target has defenses (blacklist it). Cheaper than repeatedly trying
		// to out-tech-scan a defended target.
		private async Task<int> SendDefenseProbeAttacks(int freeSlots, int slotsToLeaveFree) {
			if (!IsDefenseProbeEnabled()) return freeSlots;

			var targets = _tbotInstance.UserData.farmTargets
				.Where(t => t.State == FarmState.ProbesRequired || t.State == FarmState.FailedProbesRequired)
				.ToList();
			if (!targets.Any()) return freeSlots;

			_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
				$"ProbeAttack: sending 1 EP attack to {targets.Count} target(s) to check for defenses.");

			List<Celestial> availCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0)
				? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials)
				: _tbotInstance.UserData.celestials.ToList();

			foreach (var target in targets) {
				if (freeSlots <= slotsToLeaveFree) {
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
					if (freeSlots <= slotsToLeaveFree) break;
				}

				var ordered = availCelestials
					.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
					.ToList();

				Celestial origin = null;
				foreach (var cel in ordered) {
					var updated = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.Ships);
					if (updated.Ships.EspionageProbe >= 1) {
						origin = updated;
						break;
					}
				}
				if (origin == null) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
						$"ProbeAttack: no origin with probes for {target.Celestial.Coordinate}, skipping.");
					continue;
				}

				Ships probeShip = new();
				probeShip.Add(Buildables.EspionageProbe, 1);

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
					$"ProbeAttack: attacking {target.Celestial.Coordinate} with 1 EP from {origin.Coordinate}.");

				var fleetId = await _fleetScheduler.SendFleet(origin, probeShip, target.Celestial.Coordinate, Missions.Attack, Speeds.HundredPercent, null, _tbotInstance.UserData.userInfo.Class, false, true);

				if (fleetId > (int) SendFleetCode.GenericError) {
					freeSlots--;
					_defenseProbeFleets[target.Celestial.Coordinate.ToString()] = new DefenseProbeInfo(fleetId, DateTime.UtcNow, null, false);
					target.State = FarmState.DefenseProbing;
				} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
					break;
				}

				await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
			}
			return freeSlots;
		}

		// Resolves outstanding defense-probe-attacks from a previous cycle: if the fleet is seen
		// coming back, the probe survived (no defenses) - queue the real attack. If it's gone from the
		// fleet list without ever being seen returning, and enough time has passed since arrival that
		// it can't just be lagging behind, it was destroyed (defenses present) - blacklist the target.
		private async Task ProcessDefenseProbingResults() {
			if (!IsDefenseProbeEnabled() || _farmTargetCache == null) return;

			var probingTargets = _tbotInstance.UserData.farmTargets
				.Where(t => t.State == FarmState.DefenseProbing)
				.ToList();
			if (!probingTargets.Any()) return;

			var now = await _tbotOgameBridge.GetDateTime();
			_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

			foreach (var target in probingTargets) {
				string coordKey = target.Celestial.Coordinate.ToString();
				if (!_defenseProbeFleets.TryGetValue(coordKey, out var info)) {
					target.State = FarmState.ProbesPending;
					target.Report = null;
					continue;
				}

				var fleet = _tbotInstance.UserData.fleets.FirstOrDefault(f => f.ID == info.FleetId);

				if (fleet != null) {
					if (fleet.ReturnFlight) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ProbeAttack: EP returning from {coordKey} - no defenses, queuing attack.");
						var cacheEntry = _farmTargetCache.Get(target.Celestial.Coordinate);
						if (cacheEntry != null) {
							cacheEntry.HasDefenses = false;
							await _farmTargetCache.Upsert(cacheEntry);
						}
						// Same ScanOnly > FastFarmMode > AttackPending hierarchy as AutoFarmProcessReports -
						// a defense-probe check started before ScanOnly was turned on can still resolve
						// here after the switch; keep it from queuing an attack in that case too.
						bool scanOnly = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ScanOnly")
							&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ScanOnly;
						target.State = scanOnly ? FarmState.Idle : FarmState.AttackPending;
						_defenseProbeFleets.Remove(coordKey);
					} else if (info.ArrivalTime == null) {
						_defenseProbeFleets[coordKey] = info with { ArrivalTime = fleet.ArrivalTime };
					}
				} else {
					if (info.SeenReturning) {
						_defenseProbeFleets.Remove(coordKey);
					} else if (info.ArrivalTime.HasValue && info.ArrivalTime.Value.AddMinutes(5) < now) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ProbeAttack: EP not seen returning after arrival at {coordKey} - defenses present, blacklisting.");
						var cacheEntry = _farmTargetCache.Get(target.Celestial.Coordinate);
						if (cacheEntry != null) {
							cacheEntry.HasDefenses = true;
							await _farmTargetCache.Upsert(cacheEntry);
						}
						bool blacklistActiveProbe = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
							SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
							(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
						if (blacklistActiveProbe) {
							int resetHours = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 168);
							_farmTargetCache.Blacklist(target.Celestial.Coordinate, DateTime.UtcNow.AddHours(resetHours));
							string playerName = target.Report?.Username;
							if (string.IsNullOrEmpty(playerName) && target.Celestial is Planet planet)
								playerName = planet.Player?.Name;
							if (!string.IsNullOrEmpty(playerName)) {
								_blacklist.AddPlayer(playerName, BlacklistReason.HasDefense, resetHours);
								_farmTargetCache.BlacklistPlayer(playerName, DateTime.UtcNow.AddHours(resetHours));
							}
						}
						target.State = FarmState.NotSuitable;
						_defenseProbeFleets.Remove(coordKey);
					}
				}
			}
		}

		private double GetAcceptableFleetLossPercentage() {
			return SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "AcceptableFleetLossPercentage")
				? (double) _tbotInstance.InstanceSettings.AutoFarm.AcceptableFleetLossPercentage
				: 0;
		}

		// Warship types the user allows AutoFarm to draw on to fight through a defended target, read from
		// AutoFarm.Ships (e.g. { "Cruiser": true, "Battleship": true, ... }). Cargo/utility ships (probes,
		// cargos, recyclers, colony ships) are never included even if set to true in that same object -
		// AutoFarm.Ships lists every ship type for config discoverability, but only these fight.
		private List<Buildables> GetAllowedFarmCombatShips() {
			List<Buildables> allowed = new();
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Ships"))
				return allowed;
			var shipsSetting = _tbotInstance.InstanceSettings.AutoFarm.Ships;
			Buildables[] combatTypes = {
				Buildables.LightFighter, Buildables.HeavyFighter, Buildables.Cruiser, Buildables.Battleship,
				Buildables.Battlecruiser, Buildables.Bomber, Buildables.Destroyer, Buildables.Deathstar,
				Buildables.Reaper, Buildables.Pathfinder,
			};
			foreach (var type in combatTypes) {
				if (SettingsService.IsSettingSet(shipsSetting, type.ToString()) && (bool) shipsSetting[type.ToString()])
					allowed.Add(type);
			}
			return allowed;
		}

		// Checks whether attacking a defended target is worthwhile. Builds a combat fleet from the allowed
		// ship types available at the origin, simulates the battle against the target's reported fleet and
		// defences at increasing fleet sizes (10% steps) until one destroys the defender within the
		// acceptable loss percentage, then checks that the loot is worth the resource value of the ships
		// expected to be lost. Returns the (minimal sufficient) ships to send only if both checks pass.
		private bool TryGetAcceptableCombatFleet(EspionageReport report, Ships availableShips, out Ships combatShips, out double predictedLossPercentage, out Resources predictedDebris) {
			combatShips = new Ships();
			predictedLossPercentage = 0;
			predictedDebris = new Resources();
			double acceptableLoss = GetAcceptableFleetLossPercentage();
			if (acceptableLoss <= 0)
				return false;
			if (!report.HasFleetInformation || !report.HasDefensesInformation)
				return false;

			var allowedTypes = GetAllowedFarmCombatShips();
			Ships maxCombatShips = new Ships();
			foreach (var type in allowedTypes) {
				long available = availableShips.GetAmount(type);
				if (available > 0)
					maxCombatShips.Add(type, available);
			}
			if (maxCombatShips.IsEmpty())
				return false;

			Defences defenderDefences = new() {
				RocketLauncher = report.RocketLauncher ?? 0,
				LightLaser = report.LightLaser ?? 0,
				HeavyLaser = report.HeavyLaser ?? 0,
				GaussCannon = report.GaussCannon ?? 0,
				IonCannon = report.IonCannon ?? 0,
				PlasmaTurret = report.PlasmaTurret ?? 0,
				SmallShieldDome = report.SmallShieldDome ?? 0,
				LargeShieldDome = report.LargeShieldDome ?? 0,
			};
			Ships defenderShips = new Ships()
				.Add(Buildables.LightFighter, report.LightFighter ?? 0)
				.Add(Buildables.HeavyFighter, report.HeavyFighter ?? 0)
				.Add(Buildables.Cruiser, report.Cruiser ?? 0)
				.Add(Buildables.Battleship, report.Battleship ?? 0)
				.Add(Buildables.Battlecruiser, report.Battlecruiser ?? 0)
				.Add(Buildables.Bomber, report.Bomber ?? 0)
				.Add(Buildables.Destroyer, report.Destroyer ?? 0)
				.Add(Buildables.Deathstar, report.Deathstar ?? 0)
				.Add(Buildables.SmallCargo, report.SmallCargo ?? 0)
				.Add(Buildables.LargeCargo, report.LargeCargo ?? 0)
				.Add(Buildables.Recycler, report.Recycler ?? 0)
				.Add(Buildables.Reaper, report.Reaper ?? 0)
				.Add(Buildables.Pathfinder, report.Pathfinder ?? 0);
			Researches defenderResearches = new() {
				WeaponsTechnology = report.WeaponsTechnology ?? 0,
				ShieldingTechnology = report.ShieldingTechnology ?? 0,
				ArmourTechnology = report.ArmourTechnology ?? 0,
			};

			double minLootToRiskRatio = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootToRiskRatio")
				? (double) _tbotInstance.InstanceSettings.AutoFarm.MinLootToRiskRatio : 1.0;
			long lootValue = report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;

			// Scan fleet sizes from 10% up to 100% of what's available at the origin (rounding each ship
			// type up so small counts aren't rounded down to 0 at low fractions) and stop at the first size
			// that both destroys the defender within the acceptable loss and is worth it economically.
			Ships lastTriedShips = null;
			for (int step = 1; step <= 10; step++) {
				double fraction = step / 10.0;
				Ships candidateShips = new Ships();
				foreach (var type in allowedTypes) {
					long max = maxCombatShips.GetAmount(type);
					if (max <= 0)
						continue;
					long scaled = (long) Math.Ceiling(max * fraction);
					if (scaled > max)
						scaled = max;
					if (scaled > 0)
						candidateShips.Add(type, scaled);
				}
				if (candidateShips.IsEmpty())
					continue;
				if (lastTriedShips != null && candidateShips.ToString() == lastTriedShips.ToString())
					continue;
				lastTriedShips = candidateShips;

				var result = CombatSimulator.SimulateBattle(candidateShips, _tbotInstance.UserData.researches, defenderShips, defenderDefences, defenderResearches);

				if (!result.DefenderDestroyed)
					continue;

				if (result.AttackerLossPercentage > acceptableLoss) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: predicted fleet loss {result.AttackerLossPercentage:F1}% exceeds acceptable {acceptableLoss:F1}% even with {fraction:P0} of available combat ships, skipping.");
					return false;
				}

				double riskedValue = candidateShips.GetFleetPoints() * 1000.0 * (result.AttackerLossPercentage / 100.0);
				if (riskedValue > 0 && lootValue < riskedValue * minLootToRiskRatio) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: loot ({lootValue}) doesn't justify the resource value expected to be lost (~{riskedValue:F0}, ratio required {minLootToRiskRatio:F1}x) with {fraction:P0} of available combat ships, skipping.");
					return false;
				}

				combatShips = candidateShips;
				predictedLossPercentage = result.AttackerLossPercentage;
				predictedDebris = EstimateDebrisField(result);
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: defender destroyed in {result.Rounds} round(s) using {fraction:P0} of available combat ships, predicted fleet loss {predictedLossPercentage:F1}% (acceptable {acceptableLoss:F1}%), risked value ~{riskedValue:F0} vs loot {lootValue}, predicted debris ~{predictedDebris.TotalResources}.");
				return true;
			}

			_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: defender NOT destroyed even with 100% of available combat ships, skipping.");
			return false;
		}

		// Estimates the debris field a simulated battle would leave, from the ships (both sides) and
		// defences the simulator predicted destroyed, using real per-unit build cost (CalcPrice) and the
		// universe's actual DebrisFactor/DebrisFactorDef (ServerData). Only ever computed for defended
		// targets going through TryGetAcceptableCombatFleet - undefended targets never fight.
		private Resources EstimateDebrisField(CombatSimulationResult result) {
			Resources shipDebris = new();
			foreach (var kv in new[] { result.AttackerShipsLost, result.DefenderShipsLost }) {
				foreach (Buildables type in Enum.GetValues(typeof(Buildables))) {
					long lost = kv.GetAmount(type);
					if (lost <= 0)
						continue;
					var price = _calculationService.CalcPrice(type, 1);
					shipDebris.Metal += price.Metal * lost;
					shipDebris.Crystal += price.Crystal * lost;
				}
			}

			Resources defenceDebris = new();
			if (_tbotInstance.UserData.serverData.DebrisFactorDef > 0) {
				foreach (Buildables type in Enum.GetValues(typeof(Buildables))) {
					long lost = result.DefencesLost.GetAmount(type);
					if (lost <= 0)
						continue;
					var price = _calculationService.CalcPrice(type, 1);
					defenceDebris.Metal += price.Metal * lost;
					defenceDebris.Crystal += price.Crystal * lost;
				}
			}

			float debrisFactor = _tbotInstance.UserData.serverData.DebrisFactor;
			float debrisFactorDef = _tbotInstance.UserData.serverData.DebrisFactorDef;
			return new Resources(
				metal: (long) (shipDebris.Metal * debrisFactor + defenceDebris.Metal * debrisFactorDef),
				crystal: (long) (shipDebris.Crystal * debrisFactor + defenceDebris.Crystal * debrisFactorDef)
			);
		}

		// Persists what this report revealed into the FastFarm cache, so a future cycle has this
		// target's last-known state even before a fresh probe comes back. Resource-extrapolation-based
		// scan skipping (using this data to avoid re-probing recently-seen targets) is not wired up yet -
		// this only builds up the persistent history for now.
		private async Task CacheUpsertFromReport(EspionageReport report) {
			if (_farmTargetCache == null)
				return;
			var entry = _farmTargetCache.Get(report.Coordinate) ?? new FarmTargetCacheEntry { Coordinate = report.Coordinate };
			
			// Universe View Inactivity Detection: Espionage report confirms inactivity
			// If report says inactive, reinforce the eternal lock
			if (report.IsInactive) {
				if (!entry.IsInactive || !entry.InactiveStatusLocked) {
					entry.IsInactive = true;
					entry.InactivitySource = "espionage_report";
					entry.InactiveSince ??= DateTime.UtcNow;
					entry.InactiveStatusLocked = true; // ETERNAL persistence
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
						$"CacheUpsertFromReport: {report.Coordinate} marked INACTIVE (eternal) via espionage report");
				}
			}
			
			entry.LastReportDate = report.Date;
			entry.LastSeenDate = DateTime.UtcNow;
			entry.LastKnownResources = new Resources(report.Metal, report.Crystal, report.Deuterium);
			entry.PlayerClass = report.CharacterClass;
			if (report.HasBuildingsInformation) {
				entry.Buildings = new Buildings {
					MetalMine = report.MetalMine ?? 0,
					CrystalMine = report.CrystalMine ?? 0,
					DeuteriumSynthesizer = report.DeuteriumSynthesizer ?? 0
				};
			}
			if (report.HasDefensesInformation && report.HasFleetInformation) {
				entry.HasDefenses = !report.IsDefenceless();
				entry.HasFleet = !report.IsDefenceless();
			}
			await _farmTargetCache.Upsert(entry);
		}

		private void AddMoons(List<Celestial> scannedTargets) {
			foreach (var t in scannedTargets.ToList()) {
				var planet = t as Planet;
				if (planet == null)
					continue;
				if (planet.Moon != null) {
					Celestial tempCelestial = planet.Moon;
					// Build a fresh Coordinate instead of mutating the planet's own instance
					// (assigning then setting Type turned the planet's coordinate into a moon).
					tempCelestial.Coordinate = new Coordinate(
						t.Coordinate.Galaxy,
						t.Coordinate.System,
						t.Coordinate.Position,
						Celestials.Moon);
					scannedTargets.Add(tempCelestial);
				}
			}
		}

		private FarmTarget CheckDuplicatesAndGetExisting(Celestial planet) {
			var exists = _tbotInstance.UserData.farmTargets.Where(t => t != null && t.Celestial.HasCoords(planet.Coordinate)).ToList();
			if (exists.Count() > 1) {
				var firstExisting = exists.First();
				_tbotInstance.UserData.farmTargets.RemoveAll(c => c.Celestial.HasCoords(planet.Coordinate));
				_tbotInstance.UserData.farmTargets.Add(firstExisting);
				return firstExisting;
			}
			else if (exists.Count() == 1) {
				return exists.First();
			}
			return null;
		}

		private FarmTarget GetFarmTarget(Celestial planet) {
		bool blacklistActive = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
			SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
			(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;

		if (blacklistActive && _blacklist.IsBlacklisted(planet.Coordinate)) {
			var blacklistedTarget = _blacklist.GetBlacklistedTarget(planet.Coordinate);
			_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} is blacklisted (Reason: {blacklistedTarget.Reason}). Skipping...");
			return null;
		}

		// The SQLite target cache keeps its own, time-limited blacklist (used by the defense-probe
		// flow); honour it too so a target blacklisted there isn't picked up again.
		if (blacklistActive && _farmTargetCache != null && _farmTargetCache.IsBlacklisted(planet.Coordinate, out DateTime blacklistedUntil)) {
			_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} is blacklisted until {blacklistedUntil}. Skipping...");
			return null;
		}

		// Check player blacklist - if the player is blacklisted, skip all their planets
		string playerName = planet is Planet p ? p.Player?.Name : null;
		if (blacklistActive && _blacklist.IsPlayerBlacklisted(playerName)) {
			var blacklistedPlayer = _blacklist.GetBlacklistedPlayer(playerName);
			_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Player {playerName} is blacklisted (Reason: {blacklistedPlayer?.Reason}). Skipping {planet.ToString()}...");
			return null;
		}

		if (blacklistActive && _farmTargetCache != null && !string.IsNullOrEmpty(playerName) && _farmTargetCache.IsPlayerBlacklisted(playerName, out DateTime playerBlacklistedUntil)) {
			_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Player {playerName} is blacklisted until {playerBlacklistedUntil}. Skipping {planet.ToString()}...");
			return null;
		}

		// Check player activity - if the player is marked as active, skip ALL their coordinates
		// This avoids wasting fleets/probes on players who became active
		int playerId = planet is Planet p2 ? p2.Player?.ID ?? 0 : 0;
		if (playerId > 0 && _farmTargetCache != null) {
			if (_farmTargetCache.IsPlayerActive(playerId, out DateTime lastChecked, out int? activityDays)) {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, 
					$"Player {playerName} (ID:{playerId}) is ACTIVE per universe view (last checked: {lastChecked:u}, activity: {activityDays}d). Skipping ALL coordinates for this player: {planet.ToString()}");
				return null;
			}
		}

			var target = CheckDuplicatesAndGetExisting(planet);

			if (target == null) {
				target = new(planet, FarmState.ProbesPending);
				_tbotInstance.UserData.farmTargets.Add(target);
			} else {
				target.Celestial = planet;

				if (target.State == FarmState.Idle)
					target.State = FarmState.ProbesPending;

				if (target.State == FarmState.NotSuitable && target.Report != null) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as Not Suitable. Skipping...");
					return null;
				}

				if (target.State == FarmState.ProbesSent || target.State == FarmState.AttackPending || target.State == FarmState.AttackSent) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as {target.State.ToString()}. Skipping...");
					return null;
				}
			}
			return target;
		}

		private int GetNeededProbes(FarmTarget target) {
			int neededProbes = (int) _tbotInstance.InstanceSettings.AutoFarm.NumProbes;
			if (target.State == FarmState.ProbesRequired)
				neededProbes *= 3;
			if (target.State == FarmState.FailedProbesRequired)
				neededProbes *= 9;
			return neededProbes;
		}

		// Cargo-capable ship types AutoFarm can combine into an attack fleet, in priority order
		// (larger capacity first - "priorizar cargueiros sempre" per user, 2026-08-30). Eligibility is
		// read from the SAME AutoFarm.Ships object combat ships already use, not a separate setting -
		// user already had SmallCargo/LargeCargo marked true there expecting it to govern cargo
		// selection too (previously that object only fed GetAllowedFarmCombatShips(), cargo ships in
		// it were "for config discoverability" only and silently ignored for this purpose).
		// Pathfinder added 2026-09-12, after SmallCargo rather than before it: it has real cargo
		// capacity (10000, vs SmallCargo's 5000 and LargeCargo's 25000) and was already enabled in
		// AutoFarm.Ships, but sat unused as a cargo top-up - with LargeCargo+SmallCargo exhausted
		// mid-cycle by earlier attacks, later targets in the same run were skipped as "no suitable
		// origin" despite idle Pathfinders at that origin. Kept last (not reordered by capacity) so the
		// existing LargeCargo+SmallCargo combo above (which only ever fuses the first two entries) is
		// unchanged - Pathfinder only kicks in through the per-type fallback loop that tries each
		// allowed type standalone.
		private static readonly Buildables[] CargoCapableTypes = { Buildables.LargeCargo, Buildables.SmallCargo, Buildables.Pathfinder };

		private List<Buildables> GetAllowedFarmCargoShips() {
			List<Buildables> allowed = new();
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Ships"))
				return allowed;
			var shipsSetting = _tbotInstance.InstanceSettings.AutoFarm.Ships;
			foreach (var type in CargoCapableTypes) {
				if (SettingsService.IsSettingSet(shipsSetting, type.ToString()) && (bool) shipsSetting[type.ToString()])
					allowed.Add(type);
			}
			return allowed;
		}

		// Primary cargo type: first entry from AutoFarm.Ships (LargeCargo preferred over SmallCargo).
		// Falls back to the legacy PrimaryShip/CargoType settings only when Ships has no cargo type
		// marked true at all, so existing configs relying on those keep working unchanged.
		private Buildables GetConfiguredCargoType() {
			var allowedCargo = GetAllowedFarmCargoShips();
			if (allowedCargo.Count > 0)
				return allowedCargo[0];

			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "PrimaryShip")) {
				string primaryRaw = (string) _tbotInstance.InstanceSettings.AutoFarm.PrimaryShip;
				if (Enum.TryParse<Buildables>(primaryRaw, true, out var primaryType))
					return primaryType;
			}

			if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out var cargoType))
				return Buildables.LargeCargo;

			return cargoType;
		}

		private AutoFarmSlotBudget GetCurrentSlotBudget(Buildables cargoType) {
			return AutoFarmSlotPlanner.Calculate(
				(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
				(int) (_tbotInstance.UserData.slots?.Free ?? 0),
				(int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree,
				_tbotInstance.UserData.fleets,
				cargoType);
		}

		private class SpyOriginResult {
			public SpyOriginResult(Celestial origin, int freeSlots) {
				Origin = origin;
				BackIn = 0;
				FreeSlots = freeSlots;
			}

			public SpyOriginResult(Celestial origin, int backin, int freeSlots) {
				Origin = origin;
				BackIn = backin;
				FreeSlots = freeSlots;
			}
			public Celestial Origin { get; set; }
			public int BackIn { get; set; }
			public int FreeSlots { get; set; }
		}

		private async Task<SpyOriginResult> GetBestOrigin(List<Celestial> closestCelestials,
			Dictionary<int, long> celestialProbes,
			FarmTarget target,
			int neededProbes,
			int slotsToLeaveFree,
			int freeSlots) {
			SpyOriginResult bestOrigin = new SpyOriginResult(closestCelestials.FirstOrDefault(), int.MaxValue, freeSlots);
			foreach (var closest in closestCelestials) {
				var tempCelestial = await _tbotOgameBridge.UpdatePlanet(closest, UpdateTypes.Ships);
				celestialProbes.Remove(closest.ID);
				celestialProbes.Add(closest.ID, tempCelestial.Ships.EspionageProbe);

				if (celestialProbes[closest.ID] >= neededProbes) {
					bestOrigin = new SpyOriginResult(closest, freeSlots);
					break;
				}

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				if (freeSlots <= slotsToLeaveFree) {
					var espionageMissions = _calculationService.GetMissionsInProgress(closest.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets);
					if (espionageMissions.Any()) {
						var returningProbes = espionageMissions.Sum(f => f.Ships.EspionageProbe);
						if (celestialProbes[closest.ID] + returningProbes >= neededProbes) {
							var returningFleets = espionageMissions.OrderBy(f => f.BackIn).ToArray();
							long probesCount = 0;
							for (int i = 0; i < returningFleets.Length; i++) {
								probesCount += returningFleets[i].Ships.EspionageProbe;
								if (probesCount >= neededProbes) {
									if (bestOrigin.BackIn > returningFleets[i].BackIn)
										bestOrigin = new SpyOriginResult(closest, returningFleets[i].BackIn ?? int.MaxValue, freeSlots);
									break;
								}
							}
						}
					}
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Cannot spy {target.Celestial.Coordinate.ToString()} from {closest.Coordinate.ToString()}, insufficient probes ({celestialProbes[closest.ID]}/{neededProbes}).");
					if (bestOrigin.BackIn < int.MaxValue)
						continue;

					tempCelestial = await _tbotOgameBridge.UpdatePlanet(closest, UpdateTypes.Constructions);
					if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
						Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
						continue;
					}
					await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
					if (tempCelestial.Productions.Any(p => p.ID == (int) Buildables.EspionageProbe)) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: Probes already building.");
						continue;
					}

					await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
					var buildProbes = neededProbes - celestialProbes[closest.ID];
					var cost = _calculationService.CalcPrice(Buildables.EspionageProbe, (int) buildProbes);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
					if (tempCelestial.Resources.IsEnoughFor(cost)) {
						bestOrigin = new SpyOriginResult(closest, int.MaxValue, freeSlots);
					}
				}
			}

			if (bestOrigin.BackIn != 0) {
				if (bestOrigin.BackIn != int.MaxValue) {
					int interval = (int) ((1000 * bestOrigin.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds));
					if (interval < 0)
						interval = 1000;
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Not enough free slots {freeSlots}/{slotsToLeaveFree}. Waiting {TimeSpan.FromMilliseconds(interval)} for probes to return...");
					await Task.Delay(interval, _ct);
					bestOrigin.Origin = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
					bestOrigin.FreeSlots++;
				}
			}

			return bestOrigin;
		}

		private async Task<int> WaitForFreeSlots(int freeSlots, int slotsToLeaveFree) {
			if (freeSlots <= slotsToLeaveFree) {
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				freeSlots = _tbotInstance.UserData.slots.Free;
			}

			_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
			while (freeSlots <= slotsToLeaveFree) {
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				if (_tbotInstance.UserData.fleets.Any()) {
					// Floor of MinPollIntervalMs: short farm flights mean the "next fleet back" is
					// often already due any moment, which without a floor produced a sub-second
					// retry loop (dozens of log lines and ogamed API calls per minute) instead of
					// actually waiting for a slot to free up.
					const int MinPollIntervalMs = 5000;
					int interval = Math.Max(MinPollIntervalMs, (int) ((1000 * _tbotInstance.UserData.fleets.OrderBy(fleet => fleet.BackIn).First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond)));
					
					// MaxWaitTime: limit how long to wait for a free slot before aborting the cycle
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxWaitTime") 
						&& (int)_tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime != 0 
						&& interval > (int)_tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime * 1000) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Time to wait greater than set {(int)_tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime} seconds. Stopping autofarm.");
						return freeSlots;
					}
					
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Waiting {TimeSpan.FromMilliseconds(interval)} for fleet to return...");
					await Task.Delay(interval, _ct);
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
				} else {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, "Error: No fleet slots available and no fleets returning!");
					throw new Exception("No fleet slots available and no fleets returning!");
				}
			}
			return freeSlots;
		}

		protected override async Task Execute() {
			bool stop = false;
			bool stopAfterFullScan = false;
			bool finishedFullScan = false;
			int skippedEmptySystems = 0;

			await LogResolvedCombatReports();

			try {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Running autofarm...");
				LoadPersistedTargets();
				// Dispose the previous cycle's SQLite connection before opening a new one - a fresh
				// connection per Execute() cycle avoids holding one open indefinitely across the
				// worker's whole lifetime.
				_farmTargetCache?.Dispose();
				_farmTargetCache = await FarmTargetCache.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);
				// Initialize Universe View Inactivity Checker (replaces PlayerStatusCache /api/players.xml)
				// This is the authoritative inactivity detection for BOTH AutoFarm and FastFarm.
				_universeViewChecker = new UniverseViewInactivityChecker(_ogameService, _farmTargetCache, _tbotInstance);
				// FastFarmReportRetentionDays (Cache management): How long to keep FarmTargetCache entries that
				// were ONLY seen in galaxy scans (no espionage report). Applies to ScanOnly, FastFarm, and AutoFarm
				// modes equally. Controls the persistent cache cleanup for scan-only entries.
				// DISTINCTION: This is DIFFERENT from KeepReportFor (AutoFarm-specific espionage report retention).
				int reportRetentionDays = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmReportRetentionDays", 7);
				_farmTargetCache.ReportRetentionDays = Math.Max(1, reportRetentionDays);
				// Configure shared MinimumResources for both FastFarm and AutoFarm (persisted in FarmTargetCache)
				long minimumResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm, "MinimumResources", 1000000L);
				_farmTargetCache.MinimumResources = Math.Max(0, minimumResources);
				await _farmTargetCache.SaveSettings();
				_harvestCache?.Dispose();
				_harvestCache = await TBotDataCache.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);
				stopAfterFullScan = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan")
					&& (bool)_tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan;
				if ((bool) _tbotInstance.InstanceSettings.AutoFarm.Active) {				
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					
					int freeSlots = _tbotInstance.UserData.slots.Free;
					int slotsToLeaveFree = (int) _tbotInstance.InstanceSettings.AutoFarm.SlotsToLeaveFree;
					if (freeSlots <= slotsToLeaveFree) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to start auto farm, no slots available");
						return;
					}

					try {
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						await PruneOldReports(_tbotInstance.UserData.fleets);
						PersistTargets();

						await ProcessDefenseProbingResults();
						// Defense-probe checks are technically an Attack mission too (1 expendable
						// probe) - skip them under ScanOnly so nothing gets sent to any target at all.
						bool scanOnlyMode = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ScanOnly")
							&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ScanOnly;
						if (!scanOnlyMode)
							freeSlots = await SendDefenseProbeAttacks(freeSlots, slotsToLeaveFree);

var farmOrigins = GetFarmOrigins();
					var celestialProbes = GetCachedCelestialProbes(farmOrigins);
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

					// Process probe retries for targets that didn't return a report in time
					await ProcessProbeRetries(farmOrigins, celestialProbes, slotsToLeaveFree, freeSlots);

					int numProbed = 0;

						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Detecting farm targets...");
						bool stopAutoFarm = false;

						var scanRanges = ((IEnumerable<dynamic>)_tbotInstance.InstanceSettings.AutoFarm.ScanRange).ToList();

						var allScanRanges = ((IEnumerable<dynamic>)_tbotInstance.InstanceSettings.AutoFarm.ScanRange).ToList();
						int totalSystemsAcrossAllGalaxies = allScanRanges.Sum(r => (int)r.EndSystem - (int)r.StartSystem + 1);
						int instanceHash = Math.Abs(_tbotInstance.InstanceAlias.GetHashCode());
						int minSpacing = 499;
						int numSlotsGlobal = Math.Max(1, totalSystemsAcrossAllGalaxies / minSpacing);
						int globalSlotIndex = instanceHash % numSlotsGlobal;
						int globalOffset = globalSlotIndex * minSpacing;

						int targetGalaxy = 1;
						int targetSystem = 1;
						int remainingOffset = globalOffset;
						foreach (var r in allScanRanges) {
							int systemsInRange = (int)r.EndSystem - (int)r.StartSystem + 1;
							if (remainingOffset < systemsInRange) {
								targetGalaxy = (int)r.Galaxy;
								targetSystem = (int)r.StartSystem + remainingOffset;
								break;
							}
							remainingOffset -= systemsInRange;
						}

						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Instance: {(LogPrivacy.HideAccountInfo ? "Instance Alias" : _tbotInstance.InstanceAlias)}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Total systems across all galaxies: {totalSystemsAcrossAllGalaxies}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Num slots globally: {numSlotsGlobal}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Global slot index: {globalSlotIndex}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Global offset: {globalOffset} systems");
_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Target Galaxy: {targetGalaxy}, Target System: {targetSystem}");

						var orderedRanges = scanRanges
							.OrderBy(r => (int)r.Galaxy)
							.ThenBy(r => (int)r.StartSystem)
							.ToList();

						if (!orderedRanges.Any()) {
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "No scan ranges match galaxies with planets. Skipping AutoFarm.");
							stopAutoFarm = true;
						}

						// Build a global list of all systems to visit across all ranges,
						// then sort by distance from AutoFarm.Origin (closest first).
						var farmOriginsForScan = GetFarmOrigins();
						var allSystems = new List<(int Galaxy, int System, int RangeIndex)>();
						for (int ri = 0; ri < orderedRanges.Count; ri++) {
							var r = orderedRanges[ri];
							int galaxy = (int)r.Galaxy;
							int originalStartSystem = (int)r.StartSystem;
							int endSystem = (int)r.EndSystem;
							var systemsInRange = AutoFarmScanPlanner.EnumerateSystems(originalStartSystem, endSystem, originalStartSystem);
							foreach (int sys in systemsInRange) {
								allSystems.Add((galaxy, sys, ri));
							}
						}

						// Sort by distance from origin (closest first)
						allSystems = allSystems
							.OrderBy(t => GetMinDistanceFromOrigins(new Coordinate(t.Galaxy, t.System, 1, Celestials.Planet), farmOriginsForScan))
							.ToList();

						// If resuming, find where we left off and start from there
						int resumeIndex = 0;
						if (_tbotInstance.UserData.autoFarmLastGalaxy != 0 || _tbotInstance.UserData.autoFarmLastSystem != 0) {
							resumeIndex = allSystems.FindIndex(t => t.Galaxy == _tbotInstance.UserData.autoFarmLastGalaxy && t.System == _tbotInstance.UserData.autoFarmLastSystem);
							if (resumeIndex < 0) resumeIndex = 0;
						}

						for (int sysIdx = resumeIndex; sysIdx < allSystems.Count; sysIdx++) {
							if (stopAutoFarm)
								break;
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
								break;
							}

							int galaxy = allSystems[sysIdx].Galaxy;
							int system = allSystems[sysIdx].System;
							int rangeIndex = allSystems[sysIdx].RangeIndex;

							bool excludeSystem = ShouldExcludeSystem(galaxy, system);
							if (excludeSystem)
								continue;

							List<Celestial> scannedTargets;
							try {
								scannedTargets = await GetScannedTargetsFromGalaxy(galaxy, system);
							} catch (Exception e) {
								_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Skipping system {galaxy}:{system} after scan failure: {e.Message}");
								continue;
							}

							if (scannedTargets == null) {
								skippedEmptySystems++;
								continue;
							}

							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Found {scannedTargets.Count} targets on System {galaxy}:{system}");

							if (!scannedTargets.Any()) {
								continue;
							}

							if ((bool)_tbotInstance.InstanceSettings.AutoFarm.ExcludeMoons == false) {
								AddMoons(scannedTargets);
							}

							// Order targets by distance from AutoFarm.Origin (closest first)
							scannedTargets = scannedTargets
								.OrderBy(t => GetMinDistanceFromOrigins(t.Coordinate, farmOriginsForScan))
								.ToList();

							foreach (Celestial planet in scannedTargets) {
									if (stopAutoFarm)
										break;
									if (!await IsTargetInMinimumRank(planet, scannedTargets)) {
										continue;
									}

									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") &&
										_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0 && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Maximum number of targets to probe reached, proceeding to attack.");
										SaveScanCursor(rangeIndex, galaxy, system);
										stopAutoFarm = true;
										break;
									}

									if (ShouldExcludePlanet(planet))
										continue;

									var target = GetFarmTarget(planet);
									if (target == null)
										continue;

									List<Celestial> tempCelestials = farmOrigins;

									List<Celestial> closestCelestials = tempCelestials
										.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
										.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData)).ToList();

									if (!closestCelestials.Any()) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"No origin celestials available. Skipping target {target.Celestial.ToString()}");
										continue;
									}


									int neededProbes = GetNeededProbes(target);

									await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);

									_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
									_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
									var slotBudget = GetCurrentSlotBudget(GetConfiguredCargoType());
									if (slotBudget.AvailableSlots <= 0) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
											$"AutoFarm slot budget exhausted ({slotBudget.OwnedSlots}/{slotBudget.MaxSlots}); pausing further probes.");
										SaveScanCursor(rangeIndex, galaxy, system);
										stopAutoFarm = true;
										break;
									}

									var bestOrigin = await GetBestOrigin(closestCelestials,
										celestialProbes,
										target,
										neededProbes,
										slotsToLeaveFree,
										freeSlots);

									freeSlots = bestOrigin.FreeSlots;

									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Best origin found: {bestOrigin.Origin.Name} ({bestOrigin.Origin.Coordinate.ToString()})");

									if (_calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets).Any(f => f.Destination.IsSame(target.Celestial.Coordinate))) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Probes already on route towards {target.ToString()}.");
										continue;
									}
									if (_calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Attack, _tbotInstance.UserData.fleets).Any(f => f.Destination.IsSame(target.Celestial.Coordinate) && f.ReturnFlight == false)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Attack already on route towards {target.ToString()}.");
										continue;
									}

									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
										celestialProbes.Remove(bestOrigin.Origin.ID);
										celestialProbes.Add(bestOrigin.Origin.ID, tempCelestial.Ships.EspionageProbe);
									}

									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Insufficient probes ({celestialProbes[bestOrigin.Origin.ID]}/{neededProbes}).");
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildProbes") && _tbotInstance.InstanceSettings.AutoFarm.BuildProbes == true) {
											if (_tbotInstance.UserData.isSleeping) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {bestOrigin.Origin.ToString()}: sleep mode active, not building probes.");
												break;
											}

											var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Constructions);
											if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
												Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
												SaveScanCursor(rangeIndex, galaxy, system);
												break;
											}

											tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Productions);
											if (tempCelestial.Productions.Any(p => p.ID == (int) Buildables.EspionageProbe)) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: Probes already building.");
												SaveScanCursor(rangeIndex, galaxy, system);
												break;
											}

											var buildProbes = neededProbes - celestialProbes[bestOrigin.Origin.ID];
											var cost = _calculationService.CalcPrice(Buildables.EspionageProbe, (int) buildProbes);
											tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Resources);
											if (tempCelestial.Resources.IsEnoughFor(cost)) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Building {buildProbes}x{Buildables.EspionageProbe.ToString()}");

												try {
													await _ogameService.BuildShips(tempCelestial, Buildables.EspionageProbe, buildProbes);
													tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Facilities);
													int interval = (int) (_calculationService.CalcProductionTime(Buildables.EspionageProbe, (int) buildProbes, _tbotInstance.UserData.serverData, tempCelestial.Facilities) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Production succesfully started. Waiting {TimeSpan.FromMilliseconds(interval)} for build order to finish...");
													await Task.Delay(interval, _ct);
													// Refresh the cached probe count after the build finished, otherwise the
													// dispatch below still sees the pre-build number and skips the target.
													var rebuiltCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
													celestialProbes[bestOrigin.Origin.ID] = rebuiltCelestial.Ships.EspionageProbe;
												} catch {
													_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to start ship production.");
												}
											} else {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Not enough resources to build probes.");
												_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
												var spyMissions = _calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets);
												if (spyMissions.Any()) {
													var spyMissionToWait = spyMissions.OrderBy(c => c.BackIn).First();
													int interval = (int) ((1000 * spyMissionToWait.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond));
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for spy mission to return...");
													await Task.Delay(interval);
												} else {
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"There are not enough probes or resources to build them. Skipping this AutoFarm Execution.");
													SaveScanCursor(rangeIndex, galaxy, system);
													stopAutoFarm = true;
													break;
												}
											}
										}
									}

									if (celestialProbes[bestOrigin.Origin.ID] >= neededProbes) {

										Ships ships = new();
										ships.Add(Buildables.EspionageProbe, neededProbes);

										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Spying {target.ToString()} from {bestOrigin.Origin.ToString()} with {neededProbes} probes.");

										_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
										_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
										slotBudget = GetCurrentSlotBudget(GetConfiguredCargoType());
									if (slotBudget.AvailableSlots <= 0) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
											$"AutoFarm slot budget exhausted ({slotBudget.OwnedSlots}/{slotBudget.MaxSlots}); no probe dispatched.");
										SaveScanCursor(rangeIndex, galaxy, system);
										stopAutoFarm = true;
										break;
									}

										bestOrigin.Origin = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
										var availableProbes = bestOrigin.Origin.Ships.EspionageProbe;

										if (availableProbes < neededProbes) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
												$"Insufficient probes on {bestOrigin.Origin.ToString()} ({availableProbes}/{neededProbes}). Skipping {target.ToString()}.");
											continue;
										}

										var fleetId = await _fleetScheduler.SendFleet(bestOrigin.Origin, ships, target.Celestial.Coordinate, Missions.Spy, Speeds.HundredPercent, null, _tbotInstance.UserData.userInfo.Class, false, true);

										if (fleetId > (int) SendFleetCode.GenericError) {
											freeSlots--;
											numProbed++;
											celestialProbes[bestOrigin.Origin.ID] -= neededProbes;

											if (target.State == FarmState.ProbesRequired || target.State == FarmState.FailedProbesRequired)
												continue;

											_tbotInstance.UserData.farmTargets.Remove(target);
											target.State = FarmState.ProbesSent;
											target.LastProbeSentAt = await _tbotOgameBridge.GetDateTime();
											_tbotInstance.UserData.farmTargets.Add(target);

											continue;
										} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
											stop = true;
											return;
										} else if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Another worker took the last available slot; pausing AutoFarm instead of retrying repeatedly.");
											SaveScanCursor(rangeIndex, galaxy, system);
											stopAutoFarm = true;
											break;
										} else {
											continue;
										}
									}
								}
							}

						if (!stopAutoFarm && orderedRanges.Any()) {
								SaveScanCursor(0, 0, 0);
								 finishedFullScan = true;

								 if (!_tbotInstance.UserData.autoFarmFullScanCompleted) {
									 _tbotInstance.UserData.autoFarmFullScanCompleted = true;
									 _stateStore.MarkFullScanCompleted(DateTime.UtcNow);
									 _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
										 "First full sweep of all configured ScanRange systems completed - attacks will now be dispatched.");
								 }

								 _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
										"Full scan cycle completed, resetting scan position for next cycle");

                              if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan") &&
                               (bool)_tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan) {

									 _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									"StopAfterFullScan=true -> Full scan completed. Attacks will be processed before AutoFarm stops.");
								 }
							 }

					} catch (Exception e) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Exception: {e.Message}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to parse scan range");
					}

					if (skippedEmptySystems > 0)
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Skipped {skippedEmptySystems} systems still in the empty-system cooldown.");
					PersistTargets();

					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					Fleet firstReturning = _calculationService.GetLastReturningEspionage(_tbotInstance.UserData.fleets);
					if (firstReturning != null) {
						int interval = (int) ((1000 * firstReturning.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for all probes to return...");
						await Task.Delay(interval, _ct);
					}

					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Processing espionage reports of found inactives...");

					await AutoFarmProcessReports();
					PersistTargets();

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					Buildables configuredCargoType = GetConfiguredCargoType();
					List<RankSlotsPriority> rankSlotsPriority = new() {
						new RankSlotsPriority(Feature.BrainAutoMine,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
							((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.Transports.Active && ((bool) _tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.AutoResearch.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
							(int) _tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
						new RankSlotsPriority(Feature.Expeditions,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
							(bool) _tbotInstance.InstanceSettings.Expeditions.Active,
							(int) _tbotInstance.UserData.slots.ExpTotal,
							(int)_tbotInstance.UserData.slots.ExpInUse),
						new RankSlotsPriority(Feature.AutoFarm,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
							(bool) _tbotInstance.InstanceSettings.AutoFarm.Active,
							(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => AutoFarmSlotPlanner.IsOwnedFleet(f, configuredCargoType))),
						new RankSlotsPriority(Feature.Colonize,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize,
							(bool) _tbotInstance.InstanceSettings.AutoColonize.Active,
							(bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
								(int) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots :
								1,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
						new RankSlotsPriority(Feature.AutoDiscovery,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
							(bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active,
							(int) _tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
						new RankSlotsPriority(Feature.Harvest,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
							(bool) _tbotInstance.InstanceSettings.AutoHarvest.Active,
							(int) _tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
					};
					int MaxSlots = _calculationService.CalcSlotsPriority(Feature.AutoFarm, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);

					// AttackPending targets persist across restarts/config changes (loaded from
					// _stateStore) and were never filtered by the currently configured ScanRange - a
					// target queued before the user narrowed ScanRange stayed AttackPending forever and
					// kept getting attacked outside the new range (confirmed live 2026-08-30: ScanRange
					// set to G3:229-329, but attacks still went out to G1 targets queued earlier).
					bool IsWithinScanRange(Coordinate coord) {
						if (coord == null)
							return false;
						foreach (var r in (IEnumerable<dynamic>) _tbotInstance.InstanceSettings.AutoFarm.ScanRange) {
							if ((int) r.Galaxy == coord.Galaxy && coord.System >= (int) r.StartSystem && coord.System <= (int) r.EndSystem)
								return true;
						}
						return false;
					}

					// Shared MinimumResources for both FastFarm and AutoFarm (persisted in FarmTargetCache)
					long sharedMinimumResources = _farmTargetCache?.MinimumResources ?? 1000000L;

					// Pre-filter: exclude targets whose player is marked as active (pre-flight check)
					var activePlayerIds = _farmTargetCache?.GetActivePlayerIds() ?? new List<int>();

					bool useWeightedScore = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "UseWeightedScore") && (bool)_tbotInstance.InstanceSettings.AutoFarm.UseWeightedScore;
					double metalWeight = useWeightedScore && SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MetalWeight") ? (double)_tbotInstance.InstanceSettings.AutoFarm.MetalWeight : 1.0;
					double crystalWeight = useWeightedScore && SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "CrystalWeight") ? (double)_tbotInstance.InstanceSettings.AutoFarm.CrystalWeight : 1.0;
					double deuteriumWeight = useWeightedScore && SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "DeuteriumWeight") ? (double)_tbotInstance.InstanceSettings.AutoFarm.DeuteriumWeight : 1.0;

					var farmOriginsForAttack = GetFarmOrigins();

					List<FarmTarget> attackTargets = _tbotInstance.UserData.farmTargets
						.Where(t => t.State == FarmState.AttackPending && IsWithinScanRange(t.Celestial.Coordinate) && t.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources >= sharedMinimumResources)
						.Where(t => !(t.Celestial is Planet p && p.Player?.ID > 0 && activePlayerIds.Contains(p.Player.ID)))
						.ToList();

					if (activePlayerIds.Count > 0) {
						var filteredOut = _tbotInstance.UserData.farmTargets
							.Where(t => t.State == FarmState.AttackPending && IsWithinScanRange(t.Celestial.Coordinate) && t.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources >= sharedMinimumResources)
							.Where(t => t.Celestial is Planet p && p.Player?.ID > 0 && activePlayerIds.Contains(p.Player.ID))
							.ToList();
						foreach (var f in filteredOut) {
							var playerName = f.Celestial is Planet pl ? pl.Player?.Name : "unknown";
							var playerId = f.Celestial is Planet pl2 ? pl2.Player?.ID ?? 0 : 0;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								$"Attack dispatch: Skipping target {f.Celestial.Coordinate} - player {playerName} (ID:{playerId}) is ACTIVE per universe view");
						}
					}

					if (useWeightedScore && farmOriginsForAttack.Count > 0) {
						// Calculate weighted score for each target: (M_w*M + C_w*C + D_w*D - fuel) / flight_time
						// Using the closest origin for score calculation
						_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
						_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdateCelestials();

						foreach (var target in attackTargets) {
							var loot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class);
							var bestOrigin = farmOriginsForAttack.OrderBy(o => GetDistance(o.Coordinate, target.Celestial.Coordinate)).First();
							var originCelestial = _tbotInstance.UserData.celestials.FirstOrDefault(c => c.Coordinate.IsSame(bestOrigin.Coordinate));
							if (originCelestial == null) continue;

							var cargoShipType = configuredCargoType;
							if (cargoShipType == Buildables.Null) continue;

							var numCargo = _calculationService.CalcShipNumberForPayload(loot, cargoShipType, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, originCelestial.LFBonuses?.GetShipCargoBonus(cargoShipType) ?? 0, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
							if (numCargo <= 0) continue;

							var ships = new Ships().Add(cargoShipType, numCargo);

							// Add configured combat ships
							foreach (var shipSetting in _tbotInstance.InstanceSettings.AutoFarm.Ships) {
								if ((bool)shipSetting.Value) {
									var buildable = Enum.Parse<Buildables>(shipSetting.Key);
									if (buildable != cargoShipType && buildable != Buildables.EspionageProbe && buildable != Buildables.Recycler && buildable != Buildables.Pathfinder) {
										// Use a default count (will be refined during actual dispatch)
										ships.SetAmount(buildable, 100);
									}
								}
							}

							long flightTime = _calculationService.CalcFlightTime(bestOrigin.Coordinate, target.Celestial.Coordinate, ships, Missions.Attack, (decimal)_tbotInstance.InstanceSettings.AutoFarm.FleetSpeed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, originCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
							if (flightTime <= 0) flightTime = 1;

							long fuel = _calculationService.CalcFuelConsumption(bestOrigin.Coordinate, target.Celestial.Coordinate, ships, Missions.Attack, flightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, originCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

							double weightedValue = metalWeight * loot.Metal + crystalWeight * loot.Crystal + deuteriumWeight * loot.Deuterium;
							double score = (weightedValue - fuel) / flightTime;

							target.Score = score;
						}

						// Distance is a genuine secondary sort (ThenBy) here, not a second OrderBy - a plain
						// OrderBy after OrderByDescending doesn't compose in LINQ, it replaces the previous
						// sort outright. That silently discarded the weighted score (and the legacy
						// PreferedResource sort in the else branch below) every cycle: attacks actually
						// went out purely by proximity regardless of UseWeightedScore/PreferedResource,
						// which is why high-value targets weren't being prioritized. Reported live
						// 2026-09-17 ("fastfarm... não está buscando por alvos de alto valor").
						attackTargets = attackTargets
							.OrderByDescending(t => t.Score)
							.ThenBy(t => GetMinDistanceFromOrigins(t.Celestial.Coordinate, farmOriginsForAttack))
							.ToList();
					} else {
						// Legacy sorting by PreferedResource - see comment above, distance is ThenBy here too.
						// target.Score is also populated here now (to the same metric being sorted on) -
						// it used to stay at its default (0) in this branch, so the tie-break block below
						// treated every target as "tied at the best score" and randomized the ENTIRE list,
						// silently discarding this sort too whenever UseWeightedScore was off.
						Func<FarmTarget, long> legacyMetric = _tbotInstance.InstanceSettings.AutoFarm.PreferedResource switch {
							"Metal" => t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Metal,
							"Crystal" => t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Crystal,
							"Deuterium" => t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Deuterium,
							_ => t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources,
						};
						foreach (var t in attackTargets) t.Score = legacyMetric(t);
						attackTargets = attackTargets.OrderByDescending(legacyMetric)
							.ThenBy(t => GetMinDistanceFromOrigins(t.Celestial.Coordinate, farmOriginsForAttack)).ToList();
					}

					// Tie-break: randomize among targets within a narrow band of the best score, so
					// attacks against near-identical-value targets don't always fire in the exact same
					// order (a bit of anti-pattern noise), without diluting real value-based prioritization
					// the way a wide band does. Narrowed from ±10% to ±3% - live 2026-09-17 feedback
					// ("não está buscando os alvos melhores e mais equilibrados, mas está quase lá"): with
					// scores clustering fairly close together across many targets, a 10% band could catch
					// a large chunk of the list and shuffle away most of the ordering this method just spent
					// effort computing.
					if (attackTargets.Count > 1) {
						var bestScore = attackTargets.Max(t => t.Score);
						var threshold = bestScore * 0.97;
						var topGroup = attackTargets.Where(t => t.Score >= threshold).ToList();
						if (topGroup.Count > 1) {
							var rnd = new Random();
							var randomizedTop = topGroup.OrderBy(_ => rnd.Next()).ToList();
							var remaining = attackTargets.Where(t => t.Score < threshold).ToList();
							attackTargets = randomizedTop.Concat(remaining).ToList();
						}
					}

					bool scanOnly = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ScanOnly")
						&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ScanOnly;
					if (scanOnly) {
						// Dedicated toggle requested live 2026-09-02, replacing the Ships=all-false +
						// CargoType="LargeCargo" workaround the user had been using to the same end -
						// that combo worked but was indirect and (when CargoType got set to "Null" by
						// mistake) silently broke attack dispatch entirely. This is explicit: scan every
						// system in ScanRange (still respecting the normal cache - only outdated or
						// never-scanned systems get a live re-scan, per GetScannedTargetsFromGalaxy),
						// classify every report, but never actually dispatch a cargo attack.
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ScanOnly=true: {attackTargets.Count()} target(s) currently classified as attack-pending, not dispatched.");
						return;
					}

					if (!_tbotInstance.UserData.autoFarmFullScanCompleted) {
						// Requested live 2026-09-02: with TargetsProbedBeforeAttack=0 and a large
						// ScanRange (G1-G4), the physical fleet-slot limit (MaxSlots) forces scanning
						// to resume across many cycles - attacks used to be dispatched every cycle from
						// whatever partial results had accumulated instead of waiting for a full sweep.
						// Reports keep getting classified (AttackPending) normally in the meantime; only
						// the actual cargo dispatch below is held back, once, until autoFarmFullScanCompleted.
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"Holding {attackTargets.Count()} pending attack(s): waiting for the first full sweep of all ScanRange systems to complete before dispatching any attack.");
						return;
					}

					if (attackTargets.Count() > 0) {
						var resourceAmount = new Resources();
						attackTargets.ForEach(target => resourceAmount = resourceAmount.Sum(target.Report.Loot(_tbotInstance.UserData.userInfo.Class)));
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking suitable farm targets... (Estimated total profit: {resourceAmount.TransportableResources})");
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "No suitable targets found.");
						return;
					}

					Buildables cargoShip = configuredCargoType;
					if (cargoShip == Buildables.Null) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip is Null");
						return;
					}
					if (cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 0) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip set to EspionageProbe, but this universe does not have probe cargo.");
						return;
					}

					_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
					_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdateCelestials();
					int attackTargetsCount = 0;
					decimal lootFuelRatio = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") ? (decimal) _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio : (decimal) 0.0001;
					decimal speed = 0;

					// Adaptive throttling: delay between fleet sends based on number of active targets
					// Formula: max(500ms, 3000ms / targetCount) - fewer targets = more delay, more targets = less delay
					int throttlingDelayMs = Math.Max(500, 3000 / Math.Max(1, attackTargets.Count));

					foreach (FarmTarget target in attackTargets) {
						attackTargetsCount++;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking target {attackTargetsCount}/{attackTargets.Count()} at {target.Celestial.Coordinate.ToString()} for {target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TransportableResources}.");
						var loot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class);
						cargoShip = configuredCargoType; // reset to primary at the start of every target - see fallback below
						Celestial tempCelestial = _tbotInstance.UserData.celestials.FirstOrDefault(c => c.Coordinate.Type == Celestials.Planet);
						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
						float cargoBonus = tempCelestial.LFBonuses?.GetShipCargoBonus(cargoShip) ?? 0;
						var numCargo = _calculationService.CalcShipNumberForPayload(loot, cargoShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "CargoSurplusPercentage") && (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage > 0) {
							numCargo = (long) Math.Round(numCargo + (numCargo / 100 * (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage), 0);
						}
						var attackingShips = new Ships().Add(cargoShip, numCargo);

						List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0) ? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials) : _tbotInstance.UserData.celestials;
						List<Celestial> closestCelestials = tempCelestials
							.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
							.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
							.ToList();

						Celestial fromCelestial = null;
						foreach (var c in closestCelestials) {
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(c, UpdateTypes.Ships);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);

							// Primary type first; if this origin doesn't have enough of it alone, top up the
							// shortfall with EVERY other enabled cargo-capable type at the SAME origin (not
							// just one secondary) - a real mixed fleet, not a full switch. Requested live
							// 2026-08-30 for LargeCargo+SmallCargo, extended 2026-09-12 to combine all three
							// (LargeCargo+SmallCargo+Pathfinder) after Pathfinder-only origins were still
							// being skipped: a two-way top-up stops looking once it name-checks exactly one
							// fallback type, so an origin needing all three together to cover the loot never
							// got there. Works in capacity terms (not ship-type-specific resource math):
							// per-ship capacity is constant for a given type/LF-bonuses, so each type's
							// contribution scales linearly from a single-ship capacity probe.
							long minKeep = (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
							long availablePrimary = tempCelestial.Ships != null ? Math.Max(0, tempCelestial.Ships.GetAmount(cargoShip) - minKeep) : 0;
							bool hasEnough = availablePrimary >= numCargo;
							if (!hasEnough && tempCelestial.Ships != null) {
								var fallbackTypes = GetAllowedFarmCargoShips().Where(t => t != cargoShip).ToList();
								if (fallbackTypes.Count > 0) {
									long totalLoot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;
									long coveredCapacity = _calculationService.CalcFleetCapacity(new Ships().Add(cargoShip, availablePrimary), _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
									var combined = new Ships().Add(cargoShip, availablePrimary);
									var contributionsLog = new List<string>();
									foreach (var fallbackType in fallbackTypes) {
										if (coveredCapacity >= totalLoot) break;
										long remainingCapacity = Math.Max(0, totalLoot - coveredCapacity);
										long capacityPerShip = _calculationService.CalcFleetCapacity(new Ships().Add(fallbackType, 1), _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
										if (capacityPerShip <= 0) continue;
										long neededOfType = (long) Math.Ceiling(remainingCapacity / (double) capacityPerShip);
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "CargoSurplusPercentage") && (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage > 0) {
											neededOfType = (long) Math.Round(neededOfType + (neededOfType / 100 * (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage), 0);
										}
										long availableOfType = Math.Max(0, tempCelestial.Ships.GetAmount(fallbackType) - minKeep);
										long usedOfType = Math.Min(neededOfType, availableOfType);
										if (usedOfType <= 0) continue;
										combined.Add(fallbackType, usedOfType);
										coveredCapacity += _calculationService.CalcFleetCapacity(new Ships().Add(fallbackType, usedOfType), _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
										contributionsLog.Add($"{usedOfType} {fallbackType}");
									}
									// When the primary cargo type has zero ships available at this origin but the
									// combined fallback types alone have enough capacity, switch the reported
									// primary to whichever fallback type actually carries the fleet, instead of
									// skipping the origin or waiting to build more of the unavailable primary type.
									if (coveredCapacity >= totalLoot) {
										if (availablePrimary > 0) {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Insufficient {cargoShip} on {tempCelestial.Coordinate} ({availablePrimary}/{numCargo}), combining with {string.Join(", ", contributionsLog)}.");
										} else {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"No {cargoShip} on {tempCelestial.Coordinate}, using {string.Join(", ", contributionsLog)} instead.");
										}
										attackingShips = combined;
										numCargo = availablePrimary; // kept as the primary-type count for downstream logging only
										hasEnough = true;
									}
								}
							}
							if (hasEnough) {
								speed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
									long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
									var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
									if (optimalSpeed == 0) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

									} else {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
										speed = optimalSpeed;
									}
								}
								if (speed == 0) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
										speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
										if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
											speed = Speeds.HundredPercent;
										}
									} else {
										speed = Speeds.HundredPercent;
									}
								}
								FleetPrediction prediction = _calculationService.CalcFleetPrediction(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, Missions.Attack, speed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								if (
									(
										!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ||
										(long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime == 0 ||
										prediction.Time <= (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime
									) &&
									prediction.Fuel <= tempCelestial.Resources.Deuterium
								) {
									fromCelestial = tempCelestial;
									break;
								}
							}
						}

						if (fromCelestial == null) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"No origin celestial available near destination {target.Celestial.ToString()} with enough cargo ships.");
							foreach (var closest in closestCelestials) {
								tempCelestial = closest;
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
								speed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
									speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
									if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
										speed = Speeds.HundredPercent;
									}
								} else {
									speed = 0;
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
										long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
										var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
										if (optimalSpeed == 0) {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

										} else {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
											speed = optimalSpeed;
										}
									}
									if (speed == 0) {
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
											speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
											if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
												_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
												speed = Speeds.HundredPercent;
											}
										} else {
											speed = Speeds.HundredPercent;
										}
									}
								}
								FleetPrediction prediction = _calculationService.CalcFleetPrediction(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, Missions.Attack, speed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								if (
									tempCelestial.Ships.GetAmount(cargoShip) < numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep &&
									tempCelestial.Resources.Deuterium >= prediction.Fuel &&
									(
										!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ||
										(long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime == 0 ||
										prediction.Time <= (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime
									)
								) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildCargos") && _tbotInstance.InstanceSettings.AutoFarm.BuildCargos == true) {
										if (_tbotInstance.UserData.isSleeping) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: sleep mode active, not building {cargoShip.ToString()}.");
											continue;
										}

										tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Constructions);
										if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
											Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
											continue;
										}

										tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
										if (tempCelestial.Productions.Any()) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: a production is already in progress.");
											continue;
										}

										var neededCargos = numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep - tempCelestial.Ships.GetAmount(cargoShip);
										var cost = _calculationService.CalcPrice(cargoShip, (int) neededCargos);
										if (tempCelestial.Resources.IsEnoughFor(cost)) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Building {neededCargos}x{cargoShip.ToString()}");
										} else {
											var buildableCargos = _calculationService.CalcMaxBuildableNumber(cargoShip, tempCelestial.Resources);
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Not enough resources to build {neededCargos}x{cargoShip.ToString()}. {buildableCargos.ToString()} will be built instead.");
											neededCargos = buildableCargos;
										}

										try {
											await _ogameService.BuildShips(tempCelestial, cargoShip, neededCargos);
											tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Facilities);
											int interval = (int) (_calculationService.CalcProductionTime(cargoShip, (int) neededCargos, _tbotInstance.UserData.serverData, tempCelestial.Facilities) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Production succesfully started. Waiting {TimeSpan.FromMilliseconds(interval)} for build order to finish...");
											await Task.Delay(interval, _ct);
										} catch {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to start ship production.");
										}
									}

									if (tempCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep < (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToSend) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Insufficient {cargoShip.ToString()} on {tempCelestial.Coordinate}, require {numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep} {cargoShip.ToString()}.");
										continue;
									}

									numCargo = tempCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
									fromCelestial = tempCelestial;
									break;
								}
							}
						}

						if (fromCelestial == null) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Unable to attack {target.Celestial.Coordinate}. No suitable origin celestial available near the destination.");
							continue;
						}

						// Real cargo losses reported live 2026-09-11 (~1000 cargo ships lost in one cycle):
						// the AttackPending report backing this target can be minutes to tens of minutes
						// old by the time this dispatch loop actually reaches it (up to ~200 targets, each
						// needing several UpdatePlanet round trips before getting here) - long enough for
						// the target to log back in, build defense/fleet, and ambush the incoming cargo.
						// IsInactiveViaUniverseView() is NOT used here - it trusts the eternal DB lock and
						// never re-checks anything live for a target already locked inactive (the common
						// case), which reintroduced this exact failure mode invisibly (reported live
						// 2026-09-17). IsStillInactiveLive() always issues a fresh galaxy scan for this
						// coordinate right now, and both "active" and "unknown" abort the attack - a real
						// fleet lost here can't be undone, so an inconclusive recheck is not good enough
						// to fire on.
						var targetCoord = target.Celestial.Coordinate;
						var isInactive = await _universeViewChecker.IsStillInactiveLive(targetCoord, (target.Celestial as Planet)?.Player?.ID);
						if (isInactive != true) {
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Aborting attack on {targetCoord}: {(isInactive == false ? "player is ACTIVE per live recheck" : "live recheck was inconclusive")} (report is stale).");
							_tbotInstance.UserData.farmTargets.Remove(target);
							continue;
						}

						// IsStillInactiveLive above only re-verifies the PLAYER's activity via a fresh galaxy
						// scan - the galaxy view has no defense/fleet counts at all, so the defense check just
						// below is still reading target.Report, which can be up to KeepReportFor (1440min/24h
						// by default) old. An inactive-but-not-yet-scouted-again player can build defenses at
						// any point inside that 24h window without TBot ever noticing, since nothing here
						// forces a fresh espionage probe before firing - reported live 2026-09-18 right after
						// the combatShipsNeeded fix above ("não verifica as defesas, simplesmente ataca por
						// ser inativo"): the defense check WAS running, just against a report stale enough to
						// no longer reflect reality. Enforces a separate, much tighter cap
						// (MaxReportAgeMinutesForAttack, default 120min) than KeepReportFor's general
						// retention/probe-recheck cadence - if breached, requeues for a fresh probe instead of
						// firing on outdated defense data.
						int maxReportAgeMinutesForAttack = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm, "MaxReportAgeMinutesForAttack", 120);
						var dispatchNow = await _tbotOgameBridge.GetDateTime();
						var reportAge = dispatchNow - target.Report.Date;
						if (reportAge.TotalMinutes > maxReportAgeMinutesForAttack) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								$"Aborting attack on {targetCoord}: espionage report is {reportAge.TotalMinutes:F0}min old (max {maxReportAgeMinutesForAttack}min) - defenses may have changed since. Requesting a fresh probe.");
							target.State = FarmState.ProbesPending;
							target.Report = null;
							continue;
						}

						bool isUsingProbesForDefenceCheck = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1;
						// Combat ships the battle simulation determined are needed to clear this target's
						// defense, if any - kept in its own variable (not just merged into attackingShips
						// once here) because the cargo-type retry loop below repeatedly REPLACES
						// attackingShips wholesale (`attackingShips = new Ships().Add(cargoShip, numCargo)`)
						// while trying alternate cargo types/fleet sizes, which was silently dropping these
						// combat ships from the fleet that actually got dispatched - the simulation ran and
						// passed, but the real SendFleet went out as pure unarmed cargo against a defended
						// planet. Reported live 2026-09-18 ("atacando alvo com defesas" fleet losses).
						Ships combatShipsNeeded = new();
						if (!target.Report.IsDefenceless(isUsingProbesForDefenceCheck)) {
							if (!TryGetAcceptableCombatFleet(target.Report, fromCelestial.Ships, out Ships combatShips, out _, out _)) {
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping defended target {target.Celestial.Coordinate}: no acceptable combat fleet available at {fromCelestial}.");
								continue;
							}
							foreach (var type in GetAllowedFarmCombatShips()) {
								long qty = combatShips.GetAmount(type);
								if (qty > 0) {
									attackingShips = attackingShips.Add(type, qty);
									combatShipsNeeded = combatShipsNeeded.Add(type, qty);
								}
							}
						}

						// Ships has no Add(Ships) overload, only Add(Buildables, long) - this local
						// re-merges combatShipsNeeded (if any) into a cargo-only Ships every time the
						// retry loop below rebuilds attackingShips from scratch for a new cargo type/size.
						Ships MergeCombatShips(Ships s) {
							foreach (var type in GetAllowedFarmCombatShips()) {
								long qty = combatShipsNeeded.GetAmount(type);
								if (qty > 0) s = s.Add(type, qty);
							}
							return s;
						}

						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						freeSlots = _tbotInstance.UserData.slots.Free;
						var slotBudget = GetCurrentSlotBudget(cargoShip);
						if (MaxSlots > 0 && slotBudget.AvailableSlots > 0) {
							Buildables originalCargoShip = cargoShip;
							var allowedCargoTypes = GetAllowedFarmCargoShips();
							if (!allowedCargoTypes.Contains(cargoShip))
								allowedCargoTypes.Insert(0, cargoShip);

							bool dispatched = false;
							bool stopAttacking = false;
							// Best available capacity found across all cargo types tried below, even when
							// none of them can carry the FULL reported loot - used as a last-resort partial
							// attack instead of skipping the target outright. Confirmed live 2026-09-02: the
							// fleet only had 817 LargeCargo (28.4M capacity) + 1714 SmallCargo (11.9M) total,
							// ~40M combined, against a 542-target queue averaging 40-70M+ loot each - every
							// single one was being skipped forever because the old code required capacity to
							// cover 100% of the loot before sending anything. Taking partial loot beats
							// taking none.
							Buildables bestPartialCargo = Buildables.Null;
							long bestPartialNumCargo = 0;
							long bestPartialCapacity = 0;
							foreach (var altCargo in allowedCargoTypes) {
								cargoShip = altCargo;
								cargoBonus = tempCelestial.LFBonuses?.GetShipCargoBonus(cargoShip) ?? 0;
								numCargo = _calculationService.CalcShipNumberForPayload(loot, cargoShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "CargoSurplusPercentage") && (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage > 0) {
									numCargo = (long) Math.Round(numCargo + (numCargo / 100 * (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage), 0);
								}
								attackingShips = MergeCombatShips(new Ships().Add(cargoShip, numCargo));
								fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.Ships);
								// Pathfinder can be enabled as BOTH a combat ship and a cargo ship
								// (GetAllowedFarmCombatShips/GetAllowedFarmCargoShips both read the same
								// AutoFarm.Ships["Pathfinder"] flag) - without subtracting what the battle
								// simulation already earmarked in combatShipsNeeded, this loop could count
								// the same physical Pathfinders twice (once reserved for combat, once offered
								// up as cargo), requesting more Pathfinders total than actually exist at
								// fromCelestial. MergeCombatShips above already adds combatShipsNeeded's
								// Pathfinders into attackingShips, so availability here must only count what's
								// left AFTER that reservation. Found during agent code review, 2026-10-02.
								var availableShips = fromCelestial.Ships.GetAmount(cargoShip) - combatShipsNeeded.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
								if (availableShips <= 0) {
									if (altCargo != originalCargoShip)
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"No {altCargo} on {fromCelestial.ToString()}, trying next cargo type.");
									continue;
								}
								if (availableShips < numCargo) {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Only {availableShips} {cargoShip.ToString()} available (needed {numCargo}). Adjusting fleet size.");
									numCargo = availableShips;
									attackingShips = MergeCombatShips(new Ships().Add(cargoShip, numCargo));

									var cargoCapacity = _calculationService.CalcFleetCapacity(
										attackingShips, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology,
										fromCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
									var totalLoot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;

									if (cargoCapacity < totalLoot) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
											$"Insufficient cargo space: {numCargo} {cargoShip.ToString()} can carry {cargoCapacity:N0} but need {totalLoot:N0}. Trying next cargo type.");
										if (cargoCapacity > bestPartialCapacity) {
											bestPartialCapacity = cargoCapacity;
											bestPartialCargo = cargoShip;
											bestPartialNumCargo = numCargo;
										}
										continue;
									}
								}

								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking {target.ToString()} from {fromCelestial} with {numCargo} {cargoShip.ToString()}.");
								Ships ships = new();
								fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.LFBonuses);

								speed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
									long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
									var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(fromCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, fromCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
									if (optimalSpeed == 0) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

									} else {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
										speed = optimalSpeed;
									}
								}
								if (speed == 0) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
										speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
										if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
											speed = Speeds.HundredPercent;
										}
									} else {
										speed = Speeds.HundredPercent;
									}
								}

								var fleetId = await _fleetScheduler.SendFleet(fromCelestial, attackingShips, target.Celestial.Coordinate, Missions.Attack, speed);

								if (fleetId > (int) SendFleetCode.GenericError) {
									freeSlots--;
									dispatched = true;

									_pendingCombatReports[fleetId] = target.Celestial.Coordinate;
									_successfulTargets.RecordAttack(target.Celestial.Coordinate, loot);
									string attackedPlayerName = (target.Celestial as Planet)?.Player?.Name;
									if (_farmTargetCache != null)
										await _farmTargetCache.RecordAttack(target.Celestial.Coordinate, attackedPlayerName, loot.Metal, loot.Crystal, loot.Deuterium, DateTime.UtcNow);
									_tbotInstance.UserData.farmTargets.Remove(target);
									target.State = FarmState.AttackSent;
									target.ConsumedReportId = target.Report?.ID > 0 ? target.Report.ID : target.ConsumedReportId;
									if (target.Report?.ID > 0) {
										try {
											await _ogameService.DeleteReport(target.Report.ID);
										} catch (Exception e) {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to delete consumed espionage report {target.Report.ID}: {e.Message}");
										}
									}
									_tbotInstance.UserData.farmTargets.Add(target);
									if (!_stateStore.RecordAttack(target, loot, DateTime.UtcNow, fromCelestial.Coordinate, Missions.Attack.ToString()))
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
											$"Attack sent to {target.Celestial.Coordinate}, but it could not be recorded in the AutoFarm database.");
									PersistTargets();
									// Adaptive throttling delay between fleet sends
									if (!_ct.IsCancellationRequested)
										await Task.Delay(throttlingDelayMs, _ct);
									break;
								} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
									stop = true;
									return;
								} else if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
									_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
										"Another worker took the last available slot; keeping the attack pending for the next cycle.");
									stopAttacking = true;
									break;
								} else {
									_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
										$"Attack dispatch failed for {target.Celestial.Coordinate}; trying next cargo type.");
								}
							}

							if (!dispatched && !stopAttacking && bestPartialCargo != Buildables.Null && bestPartialNumCargo > 0) {
								cargoShip = bestPartialCargo;
								numCargo = bestPartialNumCargo;
								attackingShips = MergeCombatShips(new Ships().Add(cargoShip, numCargo));
								fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.LFBonuses);

								var totalLoot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									$"No cargo type can carry the full loot ({totalLoot:N0}) for {target.Celestial.Coordinate}; sending a partial attack with {numCargo} {cargoShip.ToString()} ({bestPartialCapacity:N0} capacity) instead of skipping.");

								decimal partialSpeed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
									partialSpeed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
									if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == partialSpeed))
										partialSpeed = Speeds.HundredPercent;
								} else {
									partialSpeed = Speeds.HundredPercent;
								}

								var partialFleetId = await _fleetScheduler.SendFleet(fromCelestial, attackingShips, target.Celestial.Coordinate, Missions.Attack, partialSpeed);
								if (partialFleetId > (int) SendFleetCode.GenericError) {
									freeSlots--;
									dispatched = true;

									_pendingCombatReports[partialFleetId] = target.Celestial.Coordinate;
									_successfulTargets.RecordAttack(target.Celestial.Coordinate, loot);
									string attackedPlayerName = (target.Celestial as Planet)?.Player?.Name;
									if (_farmTargetCache != null)
										await _farmTargetCache.RecordAttack(target.Celestial.Coordinate, attackedPlayerName, loot.Metal, loot.Crystal, loot.Deuterium, DateTime.UtcNow);
									_tbotInstance.UserData.farmTargets.Remove(target);
									target.State = FarmState.AttackSent;
									target.ConsumedReportId = target.Report?.ID > 0 ? target.Report.ID : target.ConsumedReportId;
									if (target.Report?.ID > 0) {
										try {
											await _ogameService.DeleteReport(target.Report.ID);
										} catch (Exception e) {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to delete consumed espionage report {target.Report.ID}: {e.Message}");
										}
									}
									_tbotInstance.UserData.farmTargets.Add(target);
									if (!_stateStore.RecordAttack(target, loot, DateTime.UtcNow, fromCelestial.Coordinate, Missions.Attack.ToString()))
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
											$"Attack sent to {target.Celestial.Coordinate}, but it could not be recorded in the AutoFarm database.");
									PersistTargets();
									// Adaptive throttling delay between fleet sends
									if (!_ct.IsCancellationRequested)
										await Task.Delay(throttlingDelayMs, _ct);
								} else if (partialFleetId == (int) SendFleetCode.AfterSleepTime) {
									stop = true;
									return;
								} else if (partialFleetId == (int) SendFleetCode.NotEnoughSlots) {
									_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
										"Another worker took the last available slot; keeping the attack pending for the next cycle.");
									stopAttacking = true;
								} else {
									_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
										$"Partial attack dispatch failed for {target.Celestial.Coordinate}.");
								}
							}

							if (!dispatched) {
								if (stopAttacking)
									break;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"No cargo type with available ships for target {target.Celestial.Coordinate}. Skipping target.");
								continue;
							}
						} else {
							// Log the same freeSlots value the decision above was actually based on
							// (not a fresh UserData.slots.Free re-read) - that field is shared/updated
							// concurrently by every other worker (Brain, Expeditions, etc.), so reading
							// it again here could show a completely different number than what caused
							// this "exhausted" branch to be taken, making the message actively misleading.
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								$"AutoFarm attack budget exhausted: {freeSlots} slots free (global), {slotBudget.AvailableSlots} AutoFarm slots available, {slotBudget.OwnedSlots}/{slotBudget.MaxSlots} AutoFarm slots used.");
							break;
						}
					}
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarm Exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
			} finally {
				PersistTargets();
				if (stopAfterFullScan && finishedFullScan) {
					stop = true;
				}

				if (_farmTargetCache != null) {
					await _farmTargetCache.Save();
				}

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacked targets: {_tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent).Count()}");
				_tbotInstance.UserData.farmTargets.RemoveAll(t => t.State == FarmState.ProbesSent);
				// AttackPending targets left over after the dispatch loop above (e.g. undispatchable
				// for lack of cargo ships at every origin) used to accumulate forever - never removed,
				// never expired until KeepReportFor - so each new cycle probed TargetsProbedBeforeAttack
				// MORE targets on top of an ever-growing backlog instead of the discard-and-restart
				// cycle the user expects (probe N, attack N, discard whatever's left, repeat).
				// Discard them here: the next cycle's probing will re-discover and re-classify them
				// fresh if they're still there and still profitable.
				int leftoverPending = _tbotInstance.UserData.farmTargets.Count(t => t.State == FarmState.AttackPending);
				if (leftoverPending > 0) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Discarding {leftoverPending} undispatched pending attack(s) at end of cycle.");
					_tbotInstance.UserData.farmTargets.RemoveAll(t => t.State == FarmState.AttackPending);
				}
				PersistTargets();
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Stopping feature.");
						await EndExecution();
					} else {
						var time = await _tbotOgameBridge.GetDateTime();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						long interval = RandomizeHelper.CalcRandomInterval(
							(int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMin,
							(int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMax);
						if (interval <= 0)
							interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						var newTime = time.AddMilliseconds(interval);
						ChangeWorkerPeriod(interval);
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"Next autofarm check at {newTime.ToString()} (rechecking with any remaining fleet slots).");
						await _tbotOgameBridge.CheckCelestials();
					}
				}
			}
		}

		private async Task AutoFarmProcessReports() {
			// Hierarchy requested live 2026-09-04: ScanOnly > FastFarmMode > normal AttackPending
			// classification. FastFarmMode only changes where the scanned target list comes from
			// (live scan vs cache, see GetScannedTargetsFromGalaxy) - both feed this same
			// classification step, so gating AttackPending here covers both equally. A target that
			// would otherwise become AttackPending is left/set Idle instead ("listed, no action
			// taken or to be taken") - still catalogued (loot logged, report deleted as usual),
			// just never queued for an attack while scanning only.
			bool scanOnly = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ScanOnly")
				&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ScanOnly;
			List<EspionageReportSummary> summaryReports;
			try {
				summaryReports = await _ogameService.GetEspionageReports() ?? new List<EspionageReportSummary>();
			} catch (Exception e) {
				// Report cleanup is best effort. A temporary OGame/API failure must not
				// prevent the attack phase from processing targets already marked pending.
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"Unable to retrieve espionage reports; keeping existing attack-pending targets: {e.Message}");
				return;
			}

			foreach (var summary in summaryReports) {
				if (summary == null || summary.Type == EspionageReportType.Action)
					continue;

				try {
					var report = await _ogameService.GetEspionageReport(summary.ID);
					if (report?.Coordinate == null) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
							$"Ignoring malformed espionage report {summary.ID}: no coordinate was returned.");
						continue;
					}
					if (_stateStore.HasConsumedReport(report.ID)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Ignoring consumed espionage report {report.ID} for {report.Coordinate}.");
						await _ogameService.DeleteReport(report.ID);
						continue;
					}
					if (DateTime.Compare(report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), await _tbotOgameBridge.GetDateTime()) < 0) {
						await _ogameService.DeleteReport(report.ID);
						continue;
					}

					if (_tbotInstance.UserData.farmTargets.Any(t => t.HasCoords(report.Coordinate))) {
						FarmTarget target;
						var matchingTarget = _tbotInstance.UserData.farmTargets.Where(t => t.HasCoords(report.Coordinate));
						if (matchingTarget.Count() == 0) {
							if (!report.IsInactive)
								continue;
							var galaxyInfo = await _ogameService.GetGalaxyInfo(report.Coordinate.Galaxy, report.Coordinate.System);
							var planet = galaxyInfo.Planets.FirstOrDefault(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation && p.HasCoords(report.Coordinate));
							if (planet != null) {
								target = GetFarmTarget(planet);
								if (target == null)
									continue;
							} else {
								continue;
							}
						} else {
							target = matchingTarget.First();
						}

						if (!AutoFarmAttackPolicy.CanProcessReport(target, report)) {
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
								$"Ignoring already consumed or still active espionage report {report.ID} for {report.Coordinate}.");
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						if (AutoFarmAttackPolicy.IsRepeatedNonActionableReport(target, report)) {
							// Neither this branch nor the NotSuitable/ProbesRequired/FailedProbesRequired
							// classification further below ever actually called DeleteReport - this log
							// line already said "deletion may have failed" but nothing had ever attempted
							// it in the first place. Confirmed live 2026-08-30 (same report IDs recurring
							// every cycle) that these reports simply piled up in the in-game inbox
							// forever. Actually delete it now.
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
								$"Report {report.ID} for {report.Coordinate} already classified as {target.State} in a previous cycle (nothing new to act on) - deleting duplicate copy.");
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						var newFarmTarget = target;

						if (target.Report != null && DateTime.Compare(report.Date, target.Report.Date) < 0) {
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						Buildables cargoShip;
						Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
						bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
						newFarmTarget.Report = report;
						// Reset probe retry tracking since we got a report
						newFarmTarget.ProbeRetryCount = 0;
						newFarmTarget.LastProbeSentAt = null;
						await CacheUpsertFromReport(report);
						// Use shared MinimumResources from FarmTargetCache (persisted, applies to both FastFarm and AutoFarm)
						long minimumResources = _farmTargetCache?.MinimumResources ?? 1000000L;
						var loot = report.Loot(_tbotInstance.UserData.userInfo.Class);
						if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Metal" && loot.Metal > minimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Crystal" && loot.Crystal > minimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Deuterium" && loot.Deuterium > minimumResources
							|| (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "" && loot.TotalResources > minimumResources)) {
							if (!report.HasFleetInformation || !report.HasDefensesInformation) {
								if (target.State == FarmState.ProbesRequired)
									newFarmTarget.State = FarmState.FailedProbesRequired;
								else if (target.State == FarmState.FailedProbesRequired)
									newFarmTarget.State = FarmState.NotSuitable;
								else
									newFarmTarget.State = FarmState.ProbesRequired;

								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Need more probes on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (report.IsDefenceless(isUsingProbes)) {
								newFarmTarget.State = scanOnly ? FarmState.Idle : FarmState.AttackPending;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{(scanOnly ? "Suitable target catalogued (ScanOnly)" : "Attack pending")} on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (GetAcceptableFleetLossPercentage() > 0) {
								// Defended, but the user allows fighting through a garrison within an acceptable
								// fleet-loss threshold. Feasibility (does the combat fleet at the chosen origin
								// actually beat this target within the threshold?) can only be checked once an
								// origin is picked, so it's re-evaluated in Execute() right before sending.
								newFarmTarget.State = scanOnly ? FarmState.Idle : FarmState.AttackPending;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{(scanOnly ? "Suitable target catalogued (ScanOnly)" : "Attack pending")} on {report.Coordinate} (defended - will simulate battle before sending). Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else {
								newFarmTarget.State = FarmState.NotSuitable;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - defences present.");
							bool blacklistActive = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
							if (blacklistActive) {
								int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
								_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
								_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
								if (!string.IsNullOrEmpty(report.Username)) {
									_blacklist.AddPlayer(report.Username, BlacklistReason.HasDefense, hoursUntilReset);
									_farmTargetCache?.BlacklistPlayer(report.Username, DateTime.UtcNow.AddHours(hoursUntilReset));
								}
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
							}
							}
						} else {
							newFarmTarget.State = FarmState.NotSuitable;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - insufficient loot ({report.Loot(_tbotInstance.UserData.userInfo.Class)})");
							bool blacklistActiveLowRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
							if (blacklistActiveLowRes) {
								long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long)500000);
								if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
									int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
									_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
									_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
									if (!string.IsNullOrEmpty(report.Username)) {
										_blacklist.AddPlayer(report.Username, BlacklistReason.LowResources, hoursUntilReset);
										_farmTargetCache?.BlacklistPlayer(report.Username, DateTime.UtcNow.AddHours(hoursUntilReset));
									}
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
								}
							}
						}

						// NotSuitable is terminal for this report (insufficient loot, or defended beyond
						// what the user allows) - nothing will ever act on it again, so delete it now
						// instead of waiting for a future cycle's repeat-detection to catch it (which
						// itself never actually deleted anything until the fix above). ProbesRequired/
						// FailedProbesRequired are intentionally NOT deleted here - those still expect a
						// follow-up probe-based report.
						if (newFarmTarget.State == FarmState.NotSuitable && report.ID > 0) {
							try {
								await _ogameService.DeleteReport(report.ID);
							} catch (Exception e) {
								_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to delete non-suitable espionage report {report.ID}: {e.Message}");
							}
						}

						_tbotInstance.UserData.farmTargets.Remove(target);
						_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
					} else {
					bool processAllReports = (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProcessAllReports") &&
						(bool) _tbotInstance.InstanceSettings.AutoFarm.ProcessAllReports) ||
						(SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
						SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ProcessAllReports") &&
						(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.ProcessAllReports);
					if (processAllReports && report.IsInactive) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Processing report for {report.Coordinate} not scanned by TBot (ProcessAllReports enabled)...");
						var galaxyInfo = await _ogameService.GetGalaxyInfo(report.Coordinate.Galaxy, report.Coordinate.System);
						var planet = galaxyInfo.Planets.FirstOrDefault(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation && p.HasCoords(report.Coordinate));
						if (planet != null) {
							var target = GetFarmTarget(planet);
							if (target != null) {
								if (!AutoFarmAttackPolicy.CanProcessReport(target, report)) {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
										$"Ignoring already consumed or still active espionage report {report.ID} for {report.Coordinate}.");
									await _ogameService.DeleteReport(report.ID);
									continue;
								}

								if (AutoFarmAttackPolicy.IsRepeatedNonActionableReport(target, report)) {
									// Same bug as the primary (report.HasCoords-in-farmTargets) branch above,
									// just missed there when that one was fixed: this ProcessAllReports path
									// (Blacklist.ProcessAllReports=true, confirmed active live 2026-09-02) also
									// never actually called DeleteReport - only logged that deletion "may have
									// failed" without ever attempting it. Reports piled up in-game forever.
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
										$"Report {report.ID} for {report.Coordinate} already classified as {target.State} in a previous cycle (nothing new to act on) - deleting duplicate copy.");
									await _ogameService.DeleteReport(report.ID);
									continue;
								}

								var newFarmTarget = target;
								Buildables cargoShip;
								Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
								bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
								newFarmTarget.Report = report;
								// Reset probe retry tracking since we got a report
								newFarmTarget.ProbeRetryCount = 0;
								newFarmTarget.LastProbeSentAt = null;
								await CacheUpsertFromReport(report);
								// Use shared MinimumResources from FarmTargetCache (persisted, applies to both FastFarm and AutoFarm)
								long minimumResources = _farmTargetCache?.MinimumResources ?? 1000000L;
								if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources > minimumResources) {
									if (report.HasFleetInformation && report.HasDefensesInformation) {
										if (report.IsDefenceless(isUsingProbes)) {
											newFarmTarget.State = scanOnly ? FarmState.Idle : FarmState.AttackPending;
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{(scanOnly ? "Suitable target catalogued (ScanOnly)" : "Attack pending")} on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
										} else if (GetAcceptableFleetLossPercentage() > 0) {
											newFarmTarget.State = scanOnly ? FarmState.Idle : FarmState.AttackPending;
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{(scanOnly ? "Suitable target catalogued (ScanOnly)" : "Attack pending")} on {report.Coordinate} (defended - will simulate battle before sending). Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
										} else {
											newFarmTarget.State = FarmState.NotSuitable;
											bool blacklistActiveDefense2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
												(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
											if (blacklistActiveDefense2) {
												int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
												_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
												_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
												if (!string.IsNullOrEmpty(report.Username)) {
													_blacklist.AddPlayer(report.Username, BlacklistReason.HasDefense, hoursUntilReset);
													_farmTargetCache?.BlacklistPlayer(report.Username, DateTime.UtcNow.AddHours(hoursUntilReset));
												}
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
											}
										}
									}
								} else {
									newFarmTarget.State = FarmState.NotSuitable;
									bool blacklistActiveLowRes2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
										(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
									if (blacklistActiveLowRes2) {
										long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long)500000);
										if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
											int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
											_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
											_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
											if (!string.IsNullOrEmpty(report.Username)) {
												_blacklist.AddPlayer(report.Username, BlacklistReason.LowResources, hoursUntilReset);
												_farmTargetCache?.BlacklistPlayer(report.Username, DateTime.UtcNow.AddHours(hoursUntilReset));
											}
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
										}
									}
								}

								// Same terminal-state deletion as the primary branch above - NotSuitable
								// here is reached the same way (insufficient loot or defended beyond the
								// allowed threshold) and nothing acts on it again either.
								if (newFarmTarget.State == FarmState.NotSuitable && report.ID > 0) {
									try {
										await _ogameService.DeleteReport(report.ID);
									} catch (Exception e) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to delete non-suitable espionage report {report.ID}: {e.Message}");
									}
								}

								_tbotInstance.UserData.farmTargets.Remove(target);
								_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
							}
						}
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not scanned by TBot, ignoring...");
					}
					}
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarmProcessReports Exception: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
					continue;
				}
			}

			int deleteRetries = 3;
			for (int i = 0; i < deleteRetries; i++) {
				try {
					await _ogameService.DeleteAllEspionageReports();
					break;
				} catch (Exception e) {
					var message = e.Message ?? string.Empty;
					var retryable = message.Contains("503", StringComparison.OrdinalIgnoreCase)
						|| message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
						|| message.Contains("Unable to delete", StringComparison.OrdinalIgnoreCase);

					if (retryable && i < deleteRetries - 1) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
							$"Failed to delete espionage reports ({message}), retry {i + 1}/{deleteRetries}...");
						await Task.Delay(3000, _ct);
						continue;
					}

					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
						$"Could not delete espionage reports after {i + 1} attempt(s): {message}. Attack decisions are retained for the next cycle.");
					break;
				}
			}

		}
	}
}
