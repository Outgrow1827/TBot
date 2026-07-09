using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
		private FarmTargetCache _farmTargetCache;
		private PlayersDatabase _playersDatabase;
		private bool _waitingForLootThreshold = false;
		private record DefenseProbeInfo(int FleetId, DateTime SentAt, DateTime? ArrivalTime, bool SeenReturning);
		private readonly Dictionary<string, DefenseProbeInfo> _defenseProbeFleets = new();

		// Tracks attack fleets sent by AutoFarm so that, once they return, we can fetch the real
		// combat report and correct the loot we recorded (which until then only reflects the
		// pre-attack espionage estimate, not what was actually looted).
		private record AttackFleetInfo(int FleetId, int PlayerId, string PlayerName, long EstimatedLoot, DateTime SentAt, Coordinate Coordinate);
		private readonly Dictionary<string, AttackFleetInfo> _attackFleetsPendingReport = new();

		// Manual-activity detection (previously its own ManualActivityWorker, with its own independent
		// UpdateFleets()/GetEspionageReports() poll every 5-10min). Folded into AutoFarm's own cycle so
		// the account doesn't get two independent pollers hitting ogamed on overlapping schedules - fewer
		// requests to the game server means lower ban risk. See IsManualActivityLogEnabled().
		private HashSet<int> _seenFleetIds = new();
		private HashSet<int> _seenReportIds = new();
		private bool _manualActivityStateLoaded = false;

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
		}
		public override bool IsWorkerEnabledBySettings() {
			bool autoFarmActive;
			try {
				autoFarmActive = (bool) _tbotInstance.InstanceSettings.AutoFarm.Active;
			} catch (Exception) {
				autoFarmActive = false;
			}
			// Keep the worker alive purely for manual-activity detection even if the farmer itself is
			// off, since that detection no longer has its own worker/timer to run on.
			return autoFarmActive || IsManualActivityLogEnabled();
		}

		private bool IsManualActivityLogEnabled() {
			try {
				return SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist")
					&& SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ProcessAllReports")
					&& (bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.ProcessAllReports;
			} catch (Exception) {
				return false;
			}
		}

		private string ManualActivitySeenStatePath => Path.Combine("data", $"manual_activity_seen_{_tbotInstance.InstanceAlias}.json");
		private string ManualActivityLogCsvPath => Path.Combine("data", $"manual_activity_{_tbotInstance.InstanceAlias}.csv");

		private class ManualActivitySeenState {
			public List<int> FleetIds { get; set; } = new();
			public List<int> ReportIds { get; set; } = new();
		}

		private void LoadManualActivitySeenState() {
			if (_manualActivityStateLoaded)
				return;
			_manualActivityStateLoaded = true;
			try {
				if (File.Exists(ManualActivitySeenStatePath)) {
					var state = JsonSerializer.Deserialize<ManualActivitySeenState>(File.ReadAllText(ManualActivitySeenStatePath));
					if (state != null) {
						_seenFleetIds = new HashSet<int>(state.FleetIds);
						_seenReportIds = new HashSet<int>(state.ReportIds);
					}
				}
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"Unable to load manual activity state, starting fresh: {e.Message}");
			}
		}

		private void SaveManualActivitySeenState() {
			if (!_manualActivityStateLoaded)
				return;
			try {
				Directory.CreateDirectory("data");
				// Cap growth: keep only the most recent entries, ids are monotonically increasing on the
				// server side so the highest values are also the most recent.
				const int maxTracked = 5000;
				var state = new ManualActivitySeenState {
					FleetIds = _seenFleetIds.OrderByDescending(id => id).Take(maxTracked).ToList(),
					ReportIds = _seenReportIds.OrderByDescending(id => id).Take(maxTracked).ToList(),
				};
				File.WriteAllText(ManualActivitySeenStatePath, JsonSerializer.Serialize(state));
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"Unable to save manual activity state: {e.Message}");
			}
		}

		private void AppendManualActivityCsv(string type, string from, string to, string details) {
			try {
				Directory.CreateDirectory("data");
				bool isNew = !File.Exists(ManualActivityLogCsvPath);
				using var writer = new StreamWriter(ManualActivityLogCsvPath, append: true, Encoding.UTF8);
				if (isNew)
					writer.WriteLine("Timestamp,Type,From,To,Details");
				string Escape(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
				writer.WriteLine($"{DateTime.UtcNow:O},{Escape(type)},{Escape(from)},{Escape(to)},{Escape(details)}");
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"Unable to write manual activity CSV: {e.Message}");
			}
		}

		// Manual attacks/spies: any outgoing Attack/Spy fleet whose ID we never generated ourselves.
		// Reuses whatever fleet list AutoFarm's own cycle already fetched instead of polling separately.
		private void DetectManualActivityFleets(List<Fleet> fleets) {
			if (!IsManualActivityLogEnabled() || fleets == null)
				return;
			LoadManualActivitySeenState();
			var outgoingFleets = fleets
				.Where(f => !f.ReturnFlight && (f.Mission == Missions.Attack || f.Mission == Missions.Spy || f.Mission == Missions.FederalAttack))
				.ToList();
			foreach (var fleet in outgoingFleets) {
				if (_seenFleetIds.Contains(fleet.ID))
					continue;
				_seenFleetIds.Add(fleet.ID);
				if (_tbotInstance.UserData.BotSentFleetIds.Contains(fleet.ID))
					continue;

				string type = fleet.Mission switch {
					Missions.Spy => "Manual Espionage (fleet)",
					_ => "Manual Attack",
				};
				DoLog(LogLevel.Warning, $"{type} detected: {fleet.Origin} -> {fleet.Destination} ({fleet.Ships})");
				AppendManualActivityCsv(type, fleet.Origin?.ToString(), fleet.Destination?.ToString(), fleet.Ships?.ToString());
			}
		}

		// Manual espionage reports: any report summary not accounted for by a fleet classified as
		// bot-sent above, and not already one of our own tracked farm targets. Called from the same
		// summaryReports loop AutoFarmProcessReports() already runs, so no extra GetEspionageReports() call.
		private void DetectManualActivityReport(EspionageReportSummary summary) {
			if (!IsManualActivityLogEnabled())
				return;
			LoadManualActivitySeenState();
			if (_seenReportIds.Contains(summary.ID))
				return;
			_seenReportIds.Add(summary.ID);

			bool likelyOwnFarmTarget = _tbotInstance.UserData.farmTargets != null
				&& _tbotInstance.UserData.farmTargets.ContainsKey(FarmTarget.GetKey(summary.Target));
			if (likelyOwnFarmTarget)
				return;

			DoLog(LogLevel.Warning, $"Manual Espionage detected: report on {summary.Target} from {summary.From}");
			AppendManualActivityCsv("Manual Espionage (report)", summary.From, summary.Target?.ToString(), $"LootPercentage={summary.LootPercentage}");
		}
		public override string GetWorkerName() {
			return "AutoFarm";
		}
		public override Feature GetFeature() {
			return Feature.AutoFarm;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoFarm;
		}

		private async Task PruneOldReports() {
			var newTime = await _tbotOgameBridge.GetDateTime();
			var removeReports = _tbotInstance.UserData.farmTargets.Values.Where(t => t.State != FarmState.DefenseProbing && (t.State == FarmState.AttackSent || (t.Report != null && DateTime.Compare(t.Report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), newTime) < 0))).ToList();
			foreach (var remove in removeReports) {
				// Same key (coordinate) before and after - mutating in place is enough, no need to
				// Remove()+Add() back into the dictionary like the old List<> code had to.
				remove.State = FarmState.ProbesPending;
				remove.Report = null;
			}
		}

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

		private async Task<List<Celestial>> GetScannedTargetsFromGalaxy(int galaxy, int system, GalaxyInfo prefetchedGalaxyInfo = null) {
			var galaxyInfo = prefetchedGalaxyInfo ?? await _ogameService.GetGalaxyInfo(galaxy, system);
			var planets = galaxyInfo.Planets.Where(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation);
			List<Celestial> scannedTargets = planets.Cast<Celestial>().ToList();
			await _fleetScheduler.UpdateFleets();
			//Remove all targets that are currently under attack (necessary if bot or instance is restarted)
			scannedTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));
			return scannedTargets;
		}

		/// <summary>
		/// Concurrently fetches GalaxyInfo (the public galaxy-view page - a read, not a fleet mission, so
		/// it doesn't consume fleet slots) for every non-excluded system in [startSystem, endSystem],
		/// instead of the old one-system-at-a-time sequential loop. This is what made "buscar inativos"
		/// slow on a large ScanRange: hundreds of sequential awaited HTTP calls, one per system.
		/// Concurrency is bounded by maxConcurrency (caller passes AutoFarm's currently free slot count,
		/// so the scan speeds up or slows down dynamically with how busy the account already is - a
		/// quieter account scans faster, a busy one scans more conservatively) - this is a heuristic tying
		/// scan burstiness to overall activity level, not a real fleet-slot constraint (galaxy views don't
		/// use slots), chosen deliberately to avoid a rescan being dramatically burstier than everything
		/// else the account is already doing.
		/// </summary>
		private async Task<Dictionary<int, GalaxyInfo>> PrefetchGalaxyInfo(int galaxy, int startSystem, int endSystem, int maxConcurrency) {
			var results = new Dictionary<int, GalaxyInfo>();
			var systemsToFetch = Enumerable.Range(startSystem, endSystem - startSystem + 1)
				.Where(s => !ShouldExcludeSystem(galaxy, s))
				.ToList();
			if (!systemsToFetch.Any()) return results;

			using var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
			var resultsLock = new object();
			var tasks = systemsToFetch.Select(async system => {
				await semaphore.WaitAsync(_ct);
				try {
					var info = await _ogameService.GetGalaxyInfo(galaxy, system);
					lock (resultsLock) {
						results[system] = info;
					}
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"PrefetchGalaxyInfo: failed for system {galaxy}:{system}: {e.Message}");
				} finally {
					semaphore.Release();
				}
			}).ToList();
			await Task.WhenAll(tasks);
			return results;
		}

		private bool GetFastFarmIncludeMoons() {
			return SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmIncludeMoons")
				? (bool) _tbotInstance.InstanceSettings.AutoFarm.FastFarmIncludeMoons
				: true;
		}

		private async Task CacheUpsertFromScan(Celestial celestial) {
			var planet = celestial as Planet;
			if (planet == null)
				return;
			var entry = _farmTargetCache.Get(planet.Coordinate) ?? new FarmTargetCacheEntry { Coordinate = planet.Coordinate };
			entry.PlayerName = planet.Player?.Name;
			entry.PlayerRank = planet.Player?.Rank ?? entry.PlayerRank;
			entry.IsInactive = planet.Inactive;
			entry.LastSeenDate = DateTime.UtcNow;
			entry.Temperature = planet.Temperature ?? entry.Temperature;
			await _farmTargetCache.Upsert(entry);

			if (GetFastFarmIncludeMoons() && planet.Moon != null) {
				var moonEntry = _farmTargetCache.Get(planet.Moon.Coordinate) ?? new FarmTargetCacheEntry { Coordinate = planet.Moon.Coordinate };
				moonEntry.PlayerName = planet.Player?.Name;
				moonEntry.PlayerRank = planet.Player?.Rank ?? moonEntry.PlayerRank;
				moonEntry.IsInactive = planet.Inactive;
				moonEntry.LastSeenDate = DateTime.UtcNow;
				moonEntry.Temperature = planet.Temperature ?? moonEntry.Temperature;
				await _farmTargetCache.Upsert(moonEntry);
			}
		}

		private async Task CacheUpsertFromReport(EspionageReport report) {
			var entry = _farmTargetCache.Get(report.Coordinate) ?? new FarmTargetCacheEntry { Coordinate = report.Coordinate };
			entry.IsInactive = report.IsInactive;
			entry.LastReportDate = report.Date;
			entry.LastKnownResources = new Resources(report.Metal, report.Crystal, report.Deuterium);
			// #11: keep the target's own class in sync with each fresh report, so a Collector<->General
			// switch (or any other class change) is picked up automatically on the next probe.
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
			await _farmTargetCache.Save();

			_playersDatabase.RecordSighting(0, report.Username, report.Coordinate.ToString());
			await _playersDatabase.Save();
		}

		private Resources EstimateCurrentResources(FarmTargetCacheEntry entry, DateTime now) {
			if (entry.Buildings == null || entry.LastReportDate == null || entry.LastKnownResources == null)
				return entry.LastKnownResources ?? new Resources();

			var elapsedHours = (now - entry.LastReportDate.Value).TotalHours;
			if (elapsedHours <= 0)
				return entry.LastKnownResources;

			var planetStub = new Planet {
				Coordinate = entry.Coordinate,
				Buildings = entry.Buildings,
				Temperature = entry.Temperature ?? new Temperature { Min = 20, Max = 40 }
			};
			// #11: extrapolate using the TARGET's own class (Collector gets +25% mine production), not
			// ours - using our class here was a real bug, not just a missing feature, since it made
			// every extrapolated estimate wrong for any target whose class differs from the bot's.
			var hourly = _calculationService.CalcPlanetHourlyProduction(
				planetStub,
				(int) _tbotInstance.UserData.serverData.Speed,
				researches: _tbotInstance.UserData.researches,
				playerClass: entry.PlayerClass ?? CharacterClass.NoClass);

			return entry.LastKnownResources.Sum(new Resources(
				metal: (long) (hourly.Metal * elapsedHours),
				crystal: (long) (hourly.Crystal * elapsedHours),
				deuterium: (long) (hourly.Deuterium * elapsedHours)));
		}

		private EspionageReport BuildEstimatedReport(FarmTargetCacheEntry entry, Resources estimatedResources) {
			return new EspionageReport {
				Coordinate = entry.Coordinate,
				Date = entry.LastReportDate ?? DateTime.UtcNow,
				IsInactive = entry.IsInactive,
				Metal = estimatedResources.Metal,
				Crystal = estimatedResources.Crystal,
				Deuterium = estimatedResources.Deuterium,
				HasFleetInformation = true,
				HasDefensesInformation = true,
				HasBuildingsInformation = true
			};
		}

		private bool MeetsLootThreshold(Resources loot) {
			string prefered = (string) _tbotInstance.InstanceSettings.AutoFarm.PreferedResource;
			long minimum = (long) _tbotInstance.InstanceSettings.AutoFarm.MinimumResources;
			return prefered switch {
				"Metal" => loot.Metal > minimum,
				"Crystal" => loot.Crystal > minimum,
				"Deuterium" => loot.Deuterium > minimum,
				_ => loot.TotalResources > minimum
			};
		}

		private double GetAcceptableFleetLossPercentage() {
			return SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "AcceptableFleetLossPercentage")
				? (double) _tbotInstance.InstanceSettings.AutoFarm.AcceptableFleetLossPercentage
				: 0;
		}

		/// <summary>
		/// Warship types the user allows AutoFarm to draw on to fight through a defended target, read from
		/// AutoFarm.Ships (e.g. { "Cruiser": true, "Battleship": true, ... }). Cargo/utility ships (probes,
		/// cargos, recyclers, colony ships, satellites, crawlers) are never included even if set to true.
		/// </summary>
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

		/// <summary>
		/// Checks whether attacking a defended target is worthwhile. Two things this used to NOT do, both
		/// fixed here:
		///   1. It always threw the *entire* available combat fleet at the origin into the sim, instead of
		///      sizing a fleet just big enough to win within the loss threshold - unnecessarily risking (and
		///      tying up) ships that weren't needed for this particular target.
		///   2. It only checked loss *percentage* against a flat threshold, never comparing the *absolute*
		///      value of ships predicted to be lost against the loot actually on offer - a target with tiny
		///      loot but 20% fleet loss on a huge combat fleet could pass the old check despite being a
		///      terrible trade in raw resources.
		/// Builds a combat fleet from the allowed ship types available at the origin, simulates the battle
		/// against the target's reported fleet and defences at increasing fleet sizes (10% steps) until one
		/// destroys the defender within the acceptable loss percentage, then checks that the loot is worth
		/// the resource value of the ships expected to be lost. Returns the (minimal sufficient) ships to
		/// send only if both checks pass.
		/// </summary>
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
			// type up so small counts - e.g. 3 Cruisers - aren't rounded down to 0 at low fractions) and stop
			// at the first size that both destroys the defender within the acceptable loss and is worth it
			// economically. Not a true optimum (a per-type search would be), but avoids the common case of
			// committing the whole origin's combat fleet when a fraction of it would have won just as well.
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
				// Skip re-simulating an identical fleet to the previous step (happens at low ship counts,
				// e.g. 1 Cruiser rounds up to 1 at every fraction until 100%).
				if (lastTriedShips != null && candidateShips.ToString() == lastTriedShips.ToString())
					continue;
				lastTriedShips = candidateShips;

				var result = CombatSimulator.SimulateBattle(candidateShips, _tbotInstance.UserData.researches, defenderShips, defenderDefences, defenderResearches);

				if (!result.DefenderDestroyed)
					continue; // Not enough ships yet at this fraction - try a bigger one.

				if (result.AttackerLossPercentage > acceptableLoss) {
					// More ships than this already destroy the defender but exceed the loss threshold -
					// throwing in even more ships only risks more value for the same fixed loot, so there's
					// no point trying larger fractions.
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

		/// <summary>
		/// Estimates the debris field a simulated battle would leave, from the ships (both sides) and
		/// defences the simulator predicted destroyed, using real per-unit build cost (CalcPrice) and the
		/// universe's actual DebrisFactor/DebrisFactorDef (ServerData - not guessed). Debris only forms
		/// from combat, which is why this is only ever computed for defended targets going through
		/// TryGetAcceptableCombatFleet - undefended targets never fight, so there's nothing to estimate.
		/// </summary>
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

		/// <summary>
		/// Dispatches recyclers so they land shortly (aiming for 1-10s, best-effort - see below) after the
		/// attack fleet that's expected to create this debris. A separate "suicide probe" to force debris
		/// early doesn't work mechanically (probes never fight, so they can't create debris - see
		/// HISTORICO.md 2026-07-08 sessão 8) - this is the real fix: since the debris amount is already
		/// predicted by the battle simulation, recyclers are sized and scheduled right when the attack
		/// itself is sent, instead of waiting for a later cycle to notice the debris field and only then
		/// react.
		/// Timing caveat: fleet speed is only adjustable in discrete steps (10%, or 5% for General), so an
		/// exact 1-10s landing isn't always achievable - this picks the slowest available speed whose
		/// predicted flight time is still >= the attack's, i.e. the smallest possible overshoot, and logs
		/// the actual predicted gap rather than pretending precision it doesn't have.
		/// </summary>
		private async Task TryScheduleDebrisRecycling(Celestial origin, Coordinate destination, Resources predictedDebris, int attackArriveInSeconds) {
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "RecycleDebris") ||
				!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.RecycleDebris, "Active") ||
				!(bool) _tbotInstance.InstanceSettings.AutoFarm.RecycleDebris.Active)
				return;

			long minDebris = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.RecycleDebris, "MinDebrisToRecycle")
				? (long) _tbotInstance.InstanceSettings.AutoFarm.RecycleDebris.MinDebrisToRecycle : 50000;
			if (predictedDebris.TotalResources < minDebris) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Predicted debris ({predictedDebris.TotalResources}) below configured minimum ({minDebris}), not sending recyclers.");
				return;
			}

			origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Ships);
			long availableRecyclers = origin.Ships.Recycler;
			if (availableRecyclers <= 0) {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Predicted debris ({predictedDebris.TotalResources}) at {destination} worth recycling, but no Recyclers available at {origin}.");
				return;
			}

			origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBonuses);
			float cargoBonus = origin.LFBonuses.GetShipCargoBonus(Buildables.Recycler);
			long neededRecyclers = _calculationService.CalcShipNumberForPayload(predictedDebris, Buildables.Recycler, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
			neededRecyclers = Math.Min(neededRecyclers, availableRecyclers);
			if (neededRecyclers <= 0)
				return;
			Ships recyclerShips = new Ships().Add(Buildables.Recycler, neededRecyclers);

			// Pick the slowest speed whose predicted flight time is still >= the attack's - i.e. the
			// smallest overshoot achievable at the available speed granularity.
			decimal chosenSpeed = Speeds.HundredPercent;
			long chosenTime = long.MaxValue;
			foreach (var s in _calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).OrderByDescending(s => s)) {
				var prediction = _calculationService.CalcFleetPrediction(origin.Coordinate, destination, recyclerShips, Missions.Harvest, s, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
				if (prediction.Time >= attackArriveInSeconds) {
					chosenSpeed = s;
					chosenTime = prediction.Time;
				} else {
					break; // Speeds are ordered fastest->slowest (ascending time) - once below the attack's arrival, slower speeds only take longer, so this was the best (fastest still-late-enough) option.
				}
			}

			try {
				int fleetId = await _fleetScheduler.SendFleet(origin, recyclerShips, destination, Missions.Harvest, chosenSpeed);
				if (fleetId > (int) SendFleetCode.GenericError) {
					long gapSeconds = chosenTime - attackArriveInSeconds;
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Sent {neededRecyclers} Recyclers from {origin} to {destination} for predicted debris ~{predictedDebris.TotalResources}, expected to land ~{gapSeconds}s after the attack (aiming for 1-10s, actual gap depends on available speed steps).");
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Could not send recyclers to {destination}: SendFleet returned {fleetId}.");
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Could not send recyclers to {destination}: {e.Message}");
			}
		}

		private bool _loggedMinimumPlayerRankDisabled = false;

		private bool IsTargetInMinimumRank(Celestial planet, List<Celestial> scannedTargets) {
			// P11: MinimumPlayerRank == 0 intentionally means "no rank filter" here - same sentinel
			// convention as other AutoFarm settings (MaxSensorPhalanx, TargetsProbedBeforeAttack, etc: 0 =
			// disabled/uncapped). Reviewed on 2026-07-08: not a bug, but logged once at startup so a user
			// who set 0 expecting "top rank only" (a plausible but wrong reading) notices the real behavior
			// instead of silently farming higher-rank players than intended.
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinimumPlayerRank") && (int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank == 0 && !_loggedMinimumPlayerRankDisabled) {
				_loggedMinimumPlayerRankDisabled = true;
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "AutoFarm.MinimumPlayerRank is 0 - no rank filter applied, targets of any rank are eligible.");
			}
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinimumPlayerRank") && _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank != 0) {
				if (planet as Planet == null)
					return true;
				int rank = 1;
				if (planet.Coordinate.Type == Celestials.Planet) {
					rank = (planet as Planet).Player.Rank;
				} else {
					if (scannedTargets.Any(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet)))) {
						rank = (scannedTargets.Single(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet))) as Planet).Player.Rank;
					}
				}
				if ((int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank < rank) {

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Skipping {planet.ToString()}: player has rank {rank} that is less than minimum configured {(int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank}.");
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

		private void AddMoons(List<Celestial> scannedTargets) {
			foreach (var t in scannedTargets.ToList()) {
				var planet = t as Planet;
				if (planet == null)
					continue;
				if (planet.Moon != null) {
					Celestial tempCelestial = planet.Moon;
					tempCelestial.Coordinate = t.Coordinate;
					tempCelestial.Coordinate.Type = Celestials.Moon;
					scannedTargets.Add(tempCelestial);
				}
			}
		}

		private FarmTarget CheckDuplicatesAndGetExisting(Celestial planet) {
			// O(1) now that farmTargets is keyed by coordinate - duplicates are structurally impossible
			// (a second Add() for the same coordinate just overwrites), so there's nothing left to dedupe.
			return _tbotInstance.UserData.farmTargets.TryGetValue(FarmTarget.GetKey(planet.Coordinate), out var existing) ? existing : null;
		}

		private FarmTarget GetFarmTarget(Celestial planet) {
			// Check blacklist
			try {
				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
					SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
					(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active) {
					if (_farmTargetCache.IsBlacklisted(planet.Coordinate, out DateTime blacklistedUntil)) {
						if (DateTime.Now < blacklistedUntil) {
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet} is blacklisted until {blacklistedUntil}. Skipping...");
							return null;
						} else {
							_farmTargetCache.RemoveFromBlacklist(planet.Coordinate);
						}
					}
				}
			} catch { }
			// Check if planet with coordinates exists already in _tbotInstance.UserData.farmTargets list.
			var target = CheckDuplicatesAndGetExisting(planet);

			if (target == null) {
				// Does not exist, add to _tbotInstance.UserData.farmTargets list, set state to probes pending.
				target = new(planet, FarmState.ProbesPending);
				_tbotInstance.UserData.farmTargets[FarmTarget.GetKey(planet.Coordinate)] = target;
			} else {
				// Already exists, update _tbotInstance.UserData.farmTargets list with updated planet.
				target.Celestial = planet;

				if (target.State == FarmState.Idle)
					target.State = FarmState.ProbesPending;

				// If target marked not suitable based on a non-expired espionage report, skip probing.
				if (target.State == FarmState.NotSuitable && target.Report != null) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as Not Suitable. Skipping...");
					return null;
				}

				// If probes are already sent, an attack is pending, or a defense probe attack is in flight, skip probing.
				if (target.State == FarmState.ProbesSent || target.State == FarmState.AttackPending || target.State == FarmState.DefenseProbing) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as {target.State.ToString()}. Skipping...");
					return null;
				}
			}
			return target;
		}

		/// <summary>
		/// Sizes the probe count for this target. Two layers, both additive (neither replaces the other):
		///   1. State-based escalation (pre-existing): retry with more probes after ProbesRequired/
		///      FailedProbesRequired, same as before.
		///   2. NEW: if a prior report on this target already told us its CounterEspionage level
		///      (EspionageReport.CounterEspionage - received on every report, but never read anywhere in
		///      the codebase before this), scale the probe count up proportionally. A target with high
		///      counter-espionage is more likely to detect/destroy a small probe wave, so sending more from
		///      the start (instead of only escalating *after* a wave already failed) should reduce wasted
		///      cycles - this is a directional improvement using data TBot already receives, not a claim of
		///      matching the game's exact detection-probability formula (that would need real calibration
		///      data to validate, same caution as the deuterium 1.44 constant / Crawlers formula, #13/#14).
		/// </summary>
		private int GetNeededProbes(FarmTarget target) {
			int neededProbes = (int) _tbotInstance.InstanceSettings.AutoFarm.NumProbes;
			if (target.State == FarmState.ProbesRequired)
				neededProbes *= 3;
			if (target.State == FarmState.FailedProbesRequired)
				neededProbes *= 9;

			int counterEspionage = target.Report?.CounterEspionage ?? 0;
			if (counterEspionage > 0)
				neededProbes = (int) Math.Ceiling(neededProbes * (1.0 + counterEspionage / 100.0));

			return neededProbes;
		}

		private bool IsDefenseProbeEnabled() =>
			SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProbeAttackForDefenseCheck")
			&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ProbeAttackForDefenseCheck;

		private async Task<int> SendDefenseProbeAttacks(int freeSlots, int slotsToLeaveFree) {
			if (!IsDefenseProbeEnabled()) return freeSlots;

			var targets = _tbotInstance.UserData.farmTargets.Values
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

				var fleetId = await _fleetScheduler.SendFleet(origin, probeShip, target.Celestial.Coordinate, Missions.Attack, Speeds.HundredPercent);

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

		/// <summary>
		/// P18 fix: re-spies targets currently in ProbesRequired/FailedProbesRequired state, regardless
		/// of which system the main scan loop (ScanRange sweep) is currently on. Without this, a target
		/// only gets re-probed once the sweep naturally revisits its system - on a large ScanRange that
		/// can take a very long time, so a good target (potentially 96M+ loot) just sits unprobed and its
		/// opportunity is lost. Runs every cycle, right after AutoFarmProcessReports() (so freshly
		/// resolved-to-pending targets get an immediate follow-up probe instead of waiting a full sweep).
		/// Deliberately leaner than the main scan loop's dispatch block (no "build probes if insufficient"
		/// fallback) - if this fast path can't find enough probes, the target just waits for either the
		/// main sweep or the next cycle of this same method, same as before this fix existed.
		/// </summary>
		private async Task<int> RespyPendingTargets(Dictionary<int, long> celestialProbes, int freeSlots, int slotsToLeaveFree) {
			var pendingTargets = _tbotInstance.UserData.farmTargets.Values
				.Where(t => t.State == FarmState.ProbesRequired || t.State == FarmState.FailedProbesRequired)
				.ToList();
			if (!pendingTargets.Any()) return freeSlots;

			_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

			List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0)
				? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials)
				: _tbotInstance.UserData.celestials.ToList();
			if (!tempCelestials.Any()) return freeSlots;

			foreach (var target in pendingTargets) {
				if (freeSlots <= slotsToLeaveFree) break;

				// Already got probes en route to this target from an earlier pass - don't duplicate.
				if (_calculationService.GetMissionsInProgress(target.Celestial.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets)
					.Any(f => f.Destination.IsSame(target.Celestial.Coordinate))) {
					continue;
				}

				List<Celestial> closestCelestials = tempCelestials
					.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
					.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
					.ToList();

				int neededProbes = GetNeededProbes(target);
				var bestOrigin = await GetBestOrigin(closestCelestials, celestialProbes, target, neededProbes, slotsToLeaveFree, freeSlots);
				freeSlots = bestOrigin.FreeSlots;

				if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"RespyPendingTargets: insufficient probes for {target.ToString()} ({celestialProbes[bestOrigin.Origin.ID]}/{neededProbes}) - will retry next cycle.");
					continue;
				}

				Ships ships = new();
				ships.Add(Buildables.EspionageProbe, neededProbes);

				// Use the fastest speed the origin's deuterium can actually afford - probes have very
				// little fuel margin, and at 100% speed a target a bit farther away than usual can be out
				// of reach ("sondas não tem capacidade de combustível").
				var originForSpeed = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Resources);
				decimal spySpeed = _calculationService.CalcOptimalSpyProbeSpeed(originForSpeed.Coordinate, target.Celestial.Coordinate, ships, originForSpeed.Resources.Deuterium, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, originForSpeed.LFBonuses, _tbotInstance.UserData.userInfo.Class);

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"RespyPendingTargets: re-spying {target.ToString()} from {bestOrigin.Origin.ToString()} with {neededProbes} probes at {spySpeed * 10}% speed.");
				var fleetId = await _fleetScheduler.SendFleet(bestOrigin.Origin, ships, target.Celestial.Coordinate, Missions.Spy, spySpeed);
				if (fleetId > (int) SendFleetCode.GenericError) {
					freeSlots--;
					celestialProbes[bestOrigin.Origin.ID] -= neededProbes;
				} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
					break;
				}

				await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
			}
			return freeSlots;
		}

		private async Task ProcessDefenseProbingResults() {
			if (!IsDefenseProbeEnabled()) return;

			var probingTargets = _tbotInstance.UserData.farmTargets.Values
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
						// Probe is returning home — survived the attack, no defenses.
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ProbeAttack: EP returning from {coordKey} — no defenses, queuing attack.");
						var cacheEntry = _farmTargetCache.Get(target.Celestial.Coordinate);
						if (cacheEntry != null) {
							cacheEntry.HasDefenses = false;
							await _farmTargetCache.Upsert(cacheEntry);
						}
						target.State = FarmState.AttackPending;
						_defenseProbeFleets.Remove(coordKey);
					} else if (info.ArrivalTime == null) {
						// First time seeing fleet outbound — record when it arrives at target.
						_defenseProbeFleets[coordKey] = info with { ArrivalTime = fleet.ArrivalTime };
					}
				} else {
					// Fleet gone from list (returned or destroyed).
					if (info.SeenReturning) {
						// Was seen returning on a previous cycle — already resolved.
						_defenseProbeFleets.Remove(coordKey);
					} else if (info.ArrivalTime.HasValue && info.ArrivalTime.Value.AddMinutes(5) < now) {
						// Arrived at target >5min ago and never seen returning → probe destroyed → has defenses.
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ProbeAttack: EP not seen returning after arrival at {coordKey} — defenses present, blacklisting.");
						var cacheEntry = _farmTargetCache.Get(target.Celestial.Coordinate);
						if (cacheEntry != null) {
							cacheEntry.HasDefenses = true;
							await _farmTargetCache.Upsert(cacheEntry);
						}
						try {
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active) {
								int resetHours = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours")
									? (int) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.ResetAfterHours : 168;
								_farmTargetCache.Blacklist(target.Celestial.Coordinate, DateTime.Now.AddHours(resetHours));
							}
						} catch { }
						target.State = FarmState.NotSuitable;
						_defenseProbeFleets.Remove(coordKey);
					}
					// else: fleet gone but ArrivalTime unknown or < 5min buffer — wait for next cycle.
				}
			}
		}

		/// <summary>
		/// Checks attack fleets sent by AutoFarm that have since returned home, and corrects the loot we
		/// recorded for that player with the real combat report - the espionage estimate used to queue the
		/// attack (target.Report.Loot(...)) can be significantly off from what was actually looted.
		/// </summary>
		private async Task ProcessAttackReports() {
			if (!_attackFleetsPendingReport.Any()) return;

			var resolved = new List<string>();
			foreach (var (coordKey, info) in _attackFleetsPendingReport) {
				var fleet = _tbotInstance.UserData.fleets.FirstOrDefault(f => f.ID == info.FleetId);
				if (fleet != null) {
					// Still outbound or inbound - wait for it to fully return before checking the report.
					continue;
				}

				try {
					var combatReport = await _ogameService.GetCombatReportSummary(info.FleetId);
					if (combatReport != null) {
						_playersDatabase.RecordConfirmedLoot(info.PlayerId, info.PlayerName, combatReport.Loot);
						await _playersDatabase.Save();
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"AutoFarm: confirmed loot from {coordKey} ({info.PlayerName}): {combatReport.Loot} (espionage estimate was {info.EstimatedLoot}).");

						// #8: ATTACK_HISTORY - record real loot per coordinate/player, independent of the
						// espionage-estimate-only PlayersDatabase record above.
						await _farmTargetCache.RecordAttack(info.Coordinate, info.PlayerName,
							combatReport.Metal, combatReport.Crystal, combatReport.Deuterium, DateTime.UtcNow);

						// #10: poor-farm blacklist by player (not just coordinate) - a repeat offender who
						// consistently yields below-threshold real loot gets blacklisted everywhere they own
						// a planet, not just the one coordinate we happened to hit.
						try {
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active &&
								!string.IsNullOrEmpty(info.PlayerName)) {
								long minRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist")
									? (long) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.MinimumResourcesToNotBlacklist : 0;
								int attackCount = _farmTargetCache.GetPlayerAttackCount(info.PlayerName);
								double? avgLoot = _farmTargetCache.GetAveragePlayerLoot(info.PlayerName);
								if (attackCount >= 3 && avgLoot.HasValue && avgLoot.Value < minRes) {
									int resetHours = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours")
										? (int) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.ResetAfterHours : 168;
									_farmTargetCache.BlacklistPlayer(info.PlayerName, DateTime.Now.AddHours(resetHours));
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
										$"AutoFarm: blacklisting player {info.PlayerName} for {resetHours}h - average real loot ({avgLoot.Value:F0}) below threshold across {attackCount} raids.");
								}
							}
						} catch { }
					}
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"AutoFarm: could not fetch combat report for {coordKey} (fleet {info.FleetId}): {e.Message}");
				}
				resolved.Add(coordKey);
			}

			foreach (var coordKey in resolved) {
				_attackFleetsPendingReport.Remove(coordKey);
			}
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
			//Set first celestial as default best Origin
			SpyOriginResult bestOrigin = new SpyOriginResult(closestCelestials.First(), int.MaxValue, freeSlots);
			foreach (var closest in closestCelestials) {
				// Update ships of the current planet
				var tempCelestial = await _tbotOgameBridge.UpdatePlanet(closest, UpdateTypes.Ships);
				celestialProbes.Remove(closest.ID);
				celestialProbes.Add(closest.ID, tempCelestial.Ships.EspionageProbe);

				if (celestialProbes[closest.ID] >= neededProbes) {
					//There are enough probes so it's the best origin and we can stop searching
					bestOrigin = new SpyOriginResult(closest, freeSlots);
					break;
				}

				// No probes available in this celestial
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				// If there are no free slots, update the minimum time to wait for current missions return.
				// If there are no free slots, wait for probes to come back to current celestial.
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
					//If no bestOrigin detected, the total number of probes is not enough but there are free slots, then calculate if can be built from this celestial
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Cannot spy {target.Celestial.Coordinate.ToString()} from {closest.Coordinate.ToString()}, insufficient probes ({celestialProbes[closest.ID]}/{neededProbes}).");
					if (bestOrigin.BackIn < int.MaxValue)
						continue;

					//If there is no bestOrigin, check if can be a good origin (it has enough resources to build probes)
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
				// No slots available, wait for first fleet of any mission type to return.
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				if (_tbotInstance.UserData.fleets.Any()) {
					int interval = (int) ((1000 * _tbotInstance.UserData.fleets.OrderBy(fleet => fleet.BackIn).First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond));
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Waiting {TimeSpan.FromMilliseconds(interval)} for fleet to return...");
					await Task.Delay(interval, _ct);
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
				} else {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, "Error: No fleet slots available and no fleets returning!");
					throw new Exception("No fleet slots available and no fleets returning!"); //TODO: Create custom exception
				}
			}
			return freeSlots;
		}

		/// <summary>
		/// #6 - Weighted target score: unlike plain resource totals, this weighs metal/crystal/deuterium
		/// separately (deuterium defaults heavier since it's usually the scarcest/most valuable to farm)
		/// and discounts the raw loot by flight distance to the closest celestial (a smaller haul next
		/// door often beats a bigger one across the galaxy, once the round-trip time/fuel is considered).
		/// Fuel cost itself isn't subtracted in absolute terms here (ship count for the attack isn't
		/// decided yet at sort time), but distance is a reasonable proxy for it since fuel consumption
		/// scales with distance for a fixed ship composition.
		/// </summary>
		/// <summary>
		/// Weighted target score, adapted from OGameAutomizer's real "Attack Priority" formula (found in
		/// its UI manual, 2026-07-08 sessão 10):
		///   (MetalPriority*(metal/2 - AvgLosses/3) + CrystalPriority*(crystal/2 - AvgLosses/3) +
		///    DeuteriumPriority*(deuterium/2 - fuelNeeded - AvgLosses/3)) / one-way trip time
		/// Two deliberate departures from the original, both improvements given what TBot already has:
		///   - Uses target.Report.Loot(class) instead of a hardcoded "/2" - TBot already computes the real
		///     lootable percentage (accounts for Collector bonus etc.), which is more accurate than assuming
		///     a flat 50%.
		///   - "AvgLosses/3" (OGA: a flat average historical fleet-loss amortization) is approximated here as
		///     a proportional discount on defended targets only, rather than a literal amortized average -
		///     TBot doesn't track historical fleet losses per raid (only confirmed loot, via ATTACK_HISTORY),
		///     and re-running the full battle simulator for every candidate during sorting (before an origin/
		///     ship composition is even chosen) would be expensive for no real gain, since the one target
		///     actually attacked gets properly simulated later in TryGetAcceptableCombatFleet anyway.
		/// </summary>
		private double CalcTargetScore(FarmTarget target) {
			var loot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class);
			decimal metalWeight = 1m, crystalWeight = 1m, deuteriumWeight = 1m;
			try {
				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.ScoreWeights, "Metal"))
					metalWeight = (decimal) _tbotInstance.InstanceSettings.AutoFarm.ScoreWeights.Metal;
				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.ScoreWeights, "Crystal"))
					crystalWeight = (decimal) _tbotInstance.InstanceSettings.AutoFarm.ScoreWeights.Crystal;
				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.ScoreWeights, "Deuterium"))
					deuteriumWeight = (decimal) _tbotInstance.InstanceSettings.AutoFarm.ScoreWeights.Deuterium;
			} catch { }

			List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0)
				? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials)
				: _tbotInstance.UserData.celestials;
			Celestial closest = tempCelestials
				.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
				.FirstOrDefault();

			double oneWayTripSeconds = 1;
			long fuelNeeded = 0;
			if (closest != null) {
				if (!Enum.TryParse((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out Buildables cargoShip) || cargoShip == Buildables.Null)
					cargoShip = Buildables.LargeCargo;
				// A single nominal cargo ship is enough here - flight time only depends on the slowest
				// ship's speed stat, not on fleet size (only fuel scales with ship count, and that's just a
				// rough per-target cost proxy for sorting purposes, not the real fuel of the eventual attack).
				Ships nominalShips = new Ships().Add(cargoShip, 1);
				var prediction = _calculationService.CalcFleetPrediction(closest.Coordinate, target.Celestial.Coordinate, nominalShips, Missions.Attack, Speeds.HundredPercent, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, closest.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
				oneWayTripSeconds = Math.Max(1, prediction.Time);
				fuelNeeded = prediction.Fuel;
			}

			double weightedLoot = (double) (metalWeight * loot.Metal + crystalWeight * loot.Crystal + deuteriumWeight * Math.Max(0, loot.Deuterium - fuelNeeded));

			// Approximates OGA's "AvgLosses" discount: undefended targets (the vast majority for AutoFarm)
			// get no discount, matching reality (no combat, nothing lost); defended targets are discounted
			// proportionally to how much fleet loss the user tolerates (AcceptableFleetLossPercentage) -
			// cruder than a real per-target simulation, but directionally correct and cheap to compute for
			// every candidate during sorting.
			double riskDiscount = 1.0;
			if (!target.Report.IsDefenceless())
				riskDiscount = Math.Max(0.0, 1.0 - GetAcceptableFleetLossPercentage() / 100.0);

			return weightedLoot / oneWayTripSeconds * riskDiscount;
		}

		private async Task<(bool stop, int freeSlots)> AttackPendingTargets(int freeSlots, int slotsToLeaveFree, bool applyStopAfterScan = false) {
			_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
			_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
			List<RankSlotsPriority> rankSlotsPriority = new();
			RankSlotsPriority BrainRank = new(Feature.BrainAutoMine,
				GetSlotPriority("Brain", 2),
				((bool) _tbotInstance.InstanceSettings.Brain.Active &&
					(bool) _tbotInstance.InstanceSettings.Brain.Transports.Active &&
					((bool) _tbotInstance.InstanceSettings.Brain.AutoMine.Active ||
						(bool) _tbotInstance.InstanceSettings.Brain.AutoResearch.Active ||
						(bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active ||
						(bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
				(int) _tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
				(int) _tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Transport).Count());
			RankSlotsPriority ExpeditionsRank = new(Feature.Expeditions,
				GetSlotPriority("Expeditions", 3),
				(bool) _tbotInstance.InstanceSettings.Expeditions.Active,
				(int) _tbotInstance.UserData.slots.ExpTotal,
				(int) _tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Expedition).Count());
			RankSlotsPriority AutoFarmRank = new(Feature.AutoFarm,
				GetSlotPriority("AutoFarm", 4),
				(bool) _tbotInstance.InstanceSettings.AutoFarm.Active,
				(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
				(int) _tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Attack).Count());
			RankSlotsPriority ColonizeRank = new(Feature.Colonize,
				GetSlotPriority("AutoColonize", 1),
				(bool) _tbotInstance.InstanceSettings.AutoColonize.Active,
				(bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
					(int) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots :
					1,
				(int) _tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Colonize).Count());
			RankSlotsPriority AutoDiscoveryRank = new(Feature.AutoDiscovery,
				GetSlotPriority("AutoDiscovery", 1),
				(bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active,
				(int) _tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
				(int) _tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Discovery).Count());
			RankSlotsPriority presentFeature = AutoFarmRank;
			rankSlotsPriority.Add(BrainRank);
			rankSlotsPriority.Add(ExpeditionsRank);
			rankSlotsPriority.Add(AutoFarmRank);
			rankSlotsPriority.Add(ColonizeRank);
			rankSlotsPriority.Add(AutoDiscoveryRank);
			rankSlotsPriority = rankSlotsPriority.OrderBy(r => r.Rank).ToList();
			string msg = "";
			int reservedSlots = 0;
			int MaxSlots = presentFeature.MaxSlots - presentFeature.SlotsUsed;
			int otherSlots = (int) _tbotInstance.UserData.fleets.Where(fleet => (fleet.Mission != Missions.Transport &&
					fleet.Mission != Missions.Expedition &&
					fleet.Mission != Missions.Attack &&
					fleet.Mission != Missions.Spy &&
					fleet.Mission != Missions.Colonize &&
					fleet.Mission != Missions.Discovery)
				).Count();
			foreach (RankSlotsPriority feature in rankSlotsPriority) {
				if (feature == presentFeature)
					continue;
				if (feature.Active && feature.HasPriorityOn(presentFeature)) {
					msg = $"{msg}, {feature.MaxSlots} are reserved for {feature.Feature.ToString()}";
					reservedSlots += feature.MaxSlots;
				} else {
					otherSlots += feature.SlotsUsed;
				}
			}
			if (otherSlots > 0)
				msg = $"{msg}, {otherSlots} are used for Other";
			int tempsValue = _tbotInstance.UserData.slots.Total - (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree - reservedSlots - otherSlots - presentFeature.SlotsUsed;
			tempsValue = tempsValue < 0 ? 0 : tempsValue;
			DoLog(LogLevel.Information, $"{presentFeature.MaxSlots} slots are reserved for {presentFeature.Feature.ToString()}. Total slots: {_tbotInstance.UserData.slots.Total}. {_tbotInstance.InstanceSettings.General.SlotsToLeaveFree} must remain free{msg}, {tempsValue} are availables");
			if (reservedSlots + otherSlots > _tbotInstance.UserData.slots.Total - (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
				DoLog(LogLevel.Information, $"Unable to send fleet for {presentFeature.Feature.ToString()}, too many slots are already used/reserved");
				MaxSlots = 0;
			} else if (MaxSlots > tempsValue) {
				MaxSlots = tempsValue;
				DoLog(LogLevel.Information, $"Less slots available than {presentFeature.Feature.ToString()}, many slots are already used/reserved -> steping back to {MaxSlots} instead of {presentFeature.MaxSlots}");
			}

			/// Send attacks.
			List<FarmTarget> attackTargets;
			bool useWeightedScore = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ScoreWeights") &&
				SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.ScoreWeights, "Active") &&
				(bool) _tbotInstance.InstanceSettings.AutoFarm.ScoreWeights.Active;
			if (useWeightedScore)
				attackTargets = _tbotInstance.UserData.farmTargets.Values.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => CalcTargetScore(t)).ToList();
			else if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Metal")
				attackTargets = _tbotInstance.UserData.farmTargets.Values.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Metal).ToList();
			else if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Crystal")
				attackTargets = _tbotInstance.UserData.farmTargets.Values.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Crystal).ToList();
			else if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Deuterium")
				attackTargets = _tbotInstance.UserData.farmTargets.Values.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Deuterium).ToList();
			else
				attackTargets = _tbotInstance.UserData.farmTargets.Values.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources).ToList();

			if (attackTargets.Count() > 0) {
				var resourceAmount = new Resources();
				attackTargets.ForEach(target => resourceAmount = resourceAmount.Sum(target.Report.Loot(_tbotInstance.UserData.userInfo.Class)));
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking suitable farm targets... (Estimated total profit: {resourceAmount.TransportableResources})");
			} else {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "No suitable targets found.");
				return (false, freeSlots);
			}

			Buildables cargoShip = Buildables.LargeCargo;
			if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip)) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to parse cargoShip. Falling back to default LargeCargo");
				cargoShip = Buildables.LargeCargo;
			}
			if (cargoShip == Buildables.Null) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip is Null");
				return (false, freeSlots);
			}
			if (cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 0) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip set to EspionageProbe, but this universe does not have probe cargo.");
				return (false, freeSlots);
			}
			bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1;

			_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
			_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdateCelestials();
			int attackTargetsCount = 0;
			decimal lootFuelRatio = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") ? (decimal) _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio : (decimal) 0.0001;
			decimal speed = 0;
			foreach (FarmTarget target in attackTargets) {
				attackTargetsCount++;
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking target {attackTargetsCount}/{attackTargets.Count()} at {target.Celestial.Coordinate.ToString()} for {target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TransportableResources}.");
				var loot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class);
				Celestial tempCelestial = _tbotInstance.UserData.celestials.Where(c => c.Coordinate.Type == Celestials.Planet).First();
				tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
				float cargoBonus = tempCelestial.LFBonuses.GetShipCargoBonus(cargoShip);
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
					if (tempCelestial.Ships != null && tempCelestial.Ships.GetAmount(cargoShip) >= (numCargo + _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep)) {
						speed = 0;
						if (/*cargoShip == Buildables.EspionageProbe &&*/ SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
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
							if (/*cargoShip == Buildables.EspionageProbe &&*/ SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
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

				Resources predictedDebris = new();
				if (!target.Report.IsDefenceless(isUsingProbes)) {
					if (!TryGetAcceptableCombatFleet(target.Report, fromCelestial.Ships, out Ships combatShips, out _, out predictedDebris)) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping defended target {target.Celestial.Coordinate}: no acceptable combat fleet available at {fromCelestial}.");
						continue;
					}
					foreach (var type in GetAllowedFarmCombatShips()) {
						long qty = combatShips.GetAmount(type);
						if (qty > 0)
							attackingShips = attackingShips.Add(type, qty);
					}
				}

				if (freeSlots <= slotsToLeaveFree) {
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
				}

				while (freeSlots <= slotsToLeaveFree) {
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					if (_tbotInstance.UserData.fleets.Any()) {
						int interval = (int) ((1000 * _tbotInstance.UserData.fleets.OrderBy(fleet => fleet.BackIn).First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxWaitTime") && (int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime != 0 && interval > (int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime * 1000) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Time to wait greater than set {(int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime} seconds. Stopping autofarm.");
							return (false, freeSlots);
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Waiting {TimeSpan.FromMilliseconds(interval)} for first fleet to return...");
							await Task.Delay(interval, _ct);
							_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
							freeSlots = _tbotInstance.UserData.slots.Free;
						}
					} else {
						_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, "Error: No fleet slots available and no fleets returning!");
						return (false, freeSlots);
					}
				}

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				List<Fleet> slotUsed = _tbotInstance.UserData.fleets
					.Where(fleet => fleet.Mission == Missions.Attack)
					.ToList();

				if (_tbotInstance.UserData.slots.Free > slotsToLeaveFree && slotUsed.Count() < MaxSlots) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking {target.ToString()} from {fromCelestial} with {numCargo} {cargoShip.ToString()}.");
					Ships ships = new();
					fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.LFBonuses);

					speed = 0;
					if (/*cargoShip == Buildables.EspionageProbe &&*/ SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
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
						if (target.Report != null) {
							_playersDatabase.RecordFarmed(0, target.Report.Username, target.Celestial.Coordinate.ToString());
							await _playersDatabase.Save();

							_attackFleetsPendingReport[target.Celestial.Coordinate.ToString()] = new AttackFleetInfo(
								fleetId, 0, target.Report.Username,
								target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources,
								DateTime.UtcNow, target.Celestial.Coordinate);
						}

						// Only mark as sent (and clear the cached loot estimate) on an actual successful
						// send - previously this ran unconditionally, so a failed SendFleet (e.g.
						// NotEnoughSlots/other errors) still marked the target AttackSent and it was never
						// retried.
						target.State = FarmState.AttackSent;

						var cacheEntry = _farmTargetCache.Get(target.Celestial.Coordinate);
						if (cacheEntry != null) {
							cacheEntry.LastKnownResources = null;
							await _farmTargetCache.Upsert(cacheEntry);
						}

						if (predictedDebris.TotalResources > 0) {
							_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
							var attackFleet = _tbotInstance.UserData.fleets.SingleOrDefault(f => f.ID == fleetId);
							if (attackFleet != null)
								await TryScheduleDebrisRecycling(fromCelestial, target.Celestial.Coordinate, predictedDebris, attackFleet.ArriveIn);
						}
					} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
						return (true, freeSlots);
					}
				} else {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Unable to attack {target.Celestial.Coordinate}: {slotUsed.Count()} slots used by AutoFarm, {MaxSlots} slots usable by AutoFarm, {_tbotInstance.UserData.slots.Free} slots free, {_tbotInstance.InstanceSettings.General.SlotsToLeaveFree} must remain free.");
					return (false, freeSlots);
				}
			}

			if (applyStopAfterScan && SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan") &&
				(bool) _tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan) {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "StopAfterFullScan: full scan cycle completed, stopping.");
				return (true, freeSlots);
			}
			return (false, freeSlots);
		}

		protected override async Task Execute() {
			bool stop = false;
			bool autoFarmActive;
			try {
				autoFarmActive = (bool) _tbotInstance.InstanceSettings.AutoFarm.Active;
			} catch (Exception) {
				autoFarmActive = false;
			}

			if (!autoFarmActive) {
				// AutoFarm itself is off - the only reason this worker is still running is manual-activity
				// detection (see IsWorkerEnabledBySettings/IsManualActivityLogEnabled). Skip the whole farm
				// cycle and just do the lightweight fleet/report diff, piggybacking on AutoFarm's own
				// schedule instead of running a second independent timer/poller against ogamed.
				try {
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					DetectManualActivityFleets(_tbotInstance.UserData.fleets);
					List<EspionageReportSummary> summaryReports = await _ogameService.GetEspionageReports();
					foreach (var summary in summaryReports)
						DetectManualActivityReport(summary);
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"ManualActivityLog exception: {e.Message}");
				} finally {
					SaveManualActivitySeenState();
					ChangeWorkerPeriod(RandomizeHelper.CalcRandomInterval(5, 10));
				}
				return;
			}

			// Dispose the previous cycle's SQLite connection before opening a new one - FarmTargetCache is
			// reloaded fresh every Execute() (was always the design, even back when it was a JSON file),
			// but now that it holds a real DB connection that has to be closed explicitly or it leaks.
			_farmTargetCache?.Dispose();
			_farmTargetCache = await FarmTargetCache.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);
			_playersDatabase = await PlayersDatabase.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);
			try {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Running autofarm...");
				{
					bool fastFarmMode = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmMode") && (bool) _tbotInstance.InstanceSettings.AutoFarm.FastFarmMode;
					int fastFarmMaxCacheAgeMinutes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmMaxCacheAge") ? (int) _tbotInstance.InstanceSettings.AutoFarm.FastFarmMaxCacheAge : 1440;
					var now = await _tbotOgameBridge.GetDateTime();
					if (fastFarmMode) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "FastFarm enabled: reusing cached targets instead of a live galaxy scan where possible.");
					}
					// If not enough slots are free, the farmer cannot run.
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();

					int freeSlots = _tbotInstance.UserData.slots.Free;
					int slotsToLeaveFree = (int) _tbotInstance.InstanceSettings.AutoFarm.SlotsToLeaveFree;
					if (freeSlots <= slotsToLeaveFree) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to start auto farm, no slots available");
						return;
					}

					try {
						// Prune all reports older than KeepReportFor and all reports of state AttackSent: information no longer actual.
						await PruneOldReports();

						// Piggyback manual-activity fleet detection on the fleet list AutoFarm needs anyway
						// this cycle, instead of a second independent UpdateFleets() poll.
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						DetectManualActivityFleets(_tbotInstance.UserData.fleets);

						// Check if any defense probe attacks have resolved (probe returned = no defenses, probe gone = has defenses).
						await ProcessDefenseProbingResults();

						// Check if any attack fleets have returned, and if so, correct the recorded loot
						// with the real combat report (instead of just the pre-attack espionage estimate).
						await ProcessAttackReports();

						// Convert ProbesRequired/FailedProbesRequired targets to DefenseProbing BEFORE the spy loop,
						// so the spy loop skips them and only one probe (the attack probe) is sent.
						freeSlots = await SendDefenseProbeAttacks(freeSlots, slotsToLeaveFree);

						// Attack leftover AttackPending targets from previous cycle before starting a new scan.
						// Suppressed while ProbeUntilMinimumResources is accumulating loot across cycles.
						if (!_waitingForLootThreshold && _tbotInstance.UserData.farmTargets.Values.Any(t => t.State == FarmState.AttackPending)) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Attacking leftover pending targets from previous cycle...");
							bool earlyStop;
							(earlyStop, freeSlots) = await AttackPendingTargets(freeSlots, slotsToLeaveFree, applyStopAfterScan: false);
							if (earlyStop) { stop = true; return; }
						}

						// Keep local record of _tbotInstance.UserData.celestials, to be updated by autofarmer itself, to reduce ogamed calls.
						var celestialProbes = await GetCelestialProbes();

						// Keep track of number of targets probed.
						int numProbed = 0;

						/// Galaxy scanning + target probing.
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Detecting farm targets...");
						bool stopAutoFarm = false;

						foreach (var range in _tbotInstance.InstanceSettings.AutoFarm.ScanRange) {
							if (stopAutoFarm)
								break;
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
								break;
							}

							int galaxy = (int) range.Galaxy;
							int startSystem = (int) range.StartSystem;
							int endSystem = (int) range.EndSystem;

							// Discovery speed-up: prefetch galaxy-view pages for the whole range concurrently
							// instead of one system at a time (see PrefetchGalaxyInfo for why). Concurrency is
							// tied to AutoFarm's currently free slots, minus SlotsToLeaveFree, as a "don't scan
							// drastically burstier than the account's overall activity level" heuristic - not a
							// real fleet-slot constraint, since galaxy views don't consume slots.
							Dictionary<int, GalaxyInfo> prefetchedGalaxyInfo = null;
							if (!fastFarmMode) {
								int scanConcurrency = Math.Max(1, freeSlots - slotsToLeaveFree);
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Prefetching galaxy info for systems {startSystem}-{endSystem} in galaxy {galaxy} (concurrency: {scanConcurrency})...");
								prefetchedGalaxyInfo = await PrefetchGalaxyInfo(galaxy, startSystem, endSystem, scanConcurrency);
							}

							// Loop from start to end system.
							for (var system = startSystem; system <= endSystem; system++) {

								if (stopAutoFarm)
									break;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Maximum number of targets to probe reached, proceeding to attack.");
									break;
								}

								// Check excluded system.
								bool excludeSystem = ShouldExcludeSystem(galaxy, system);
								if (excludeSystem)
									continue;

							List<Celestial> scannedTargets;
							List<FarmTargetCacheEntry> fastFarmEntries = null;
							if (fastFarmMode) {
								int minRank = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinimumPlayerRank") ? (int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank : 0;
								fastFarmEntries = _farmTargetCache.GetInRange(galaxy, system, system)
									.Where(e => e.IsInactive && (minRank == 0 || e.PlayerRank <= minRank))
									.ToList();
								if (!GetFastFarmIncludeMoons()) {
									fastFarmEntries = fastFarmEntries.Where(e => e.Coordinate.Type == Celestials.Planet).ToList();
								}
								scannedTargets = fastFarmEntries.Select(e => new Celestial { Coordinate = e.Coordinate, Name = e.PlayerName ?? "" }).ToList();
								_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
								scannedTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));
							} else {
								prefetchedGalaxyInfo.TryGetValue(system, out var prefetchedInfo);
								scannedTargets = await GetScannedTargetsFromGalaxy(galaxy, system, prefetchedInfo);
								foreach (var found in scannedTargets)
									await CacheUpsertFromScan(found);
							}

							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Found {scannedTargets.Count} targets on System {galaxy}:{system}{(fastFarmMode ? " (from FastFarm cache)" : "")}");

							if (!scannedTargets.Any())
								continue;

							if (!fastFarmMode && (bool) _tbotInstance.InstanceSettings.AutoFarm.ExcludeMoons == false) {
								AddMoons(scannedTargets);
							}

								// Add each planet that has inactive status to _tbotInstance.UserData.farmTargets.
								foreach (Celestial planet in scannedTargets) {
									if (stopAutoFarm)
										break;
									// Check if target is below set minimum rank.
									if (!IsTargetInMinimumRank(planet, scannedTargets)) {
										continue;
									}

									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") &&
										_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0 && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Maximum number of targets to probe reached, proceeding to attack.");
										break;
									}

									// Check excluded planet.
									if (ShouldExcludePlanet(planet))
										continue;

									// Manage existing Target or generate a new one. If should not be processed returns null
									var target = GetFarmTarget(planet);
									if (target == null)
										continue;

								if (target.State == FarmState.ProbesPending) {
									// #7: ScanRange changes shouldn't force a re-probe of a target that already has
									// a fresh cached report - e.g. widening the range re-includes a system already
									// probed in a previous cycle, or a system briefly excluded and re-added. Applies
									// to both FastFarmMode (which already had this) and normal mode (which used to
									// always fall through to a fresh probe here, ignoring any existing cache entry).
									var cacheEntry = fastFarmMode
										? fastFarmEntries?.FirstOrDefault(e => e.HasCoords(planet.Coordinate))
										: _farmTargetCache.Get(planet.Coordinate);
									if (cacheEntry != null && cacheEntry.LastReportDate != null
										&& (now - cacheEntry.LastReportDate.Value).TotalMinutes <= fastFarmMaxCacheAgeMinutes) {
										if (cacheEntry.HasDefenses == true) {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"FastFarm: skipping {target.ToString()} due to known defenses.");
											continue;
										}
										var estimated = EstimateCurrentResources(cacheEntry, now);
										var syntheticReport = BuildEstimatedReport(cacheEntry, estimated);
										var loot = syntheticReport.Loot(_tbotInstance.UserData.userInfo.Class);
										if (MeetsLootThreshold(loot)) {
											if (cacheEntry.HasDefenses == null)
												_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"FastFarm: using unconfirmed no-defenses estimate for {target.ToString()}. Skipping probe based on extrapolation only.");
											else
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"FastFarm: using extrapolated data for {target.ToString()}, skipping probe. Estimated loot: {loot}");
											target.Report = syntheticReport;
											target.State = FarmState.AttackPending;
												continue;
											}
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"FastFarm: cached estimate for {target.ToString()} below threshold, falling back to probing.");
										}
									}

									// Send spy probe from closest celestial with available probes to the target.
									List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0) ? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials) : _tbotInstance.UserData.celestials;
									List<Celestial> closestCelestials = tempCelestials
										.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
										.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData)).ToList();


									int neededProbes = GetNeededProbes(target);

									await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);

									_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
									var probesInMission = _tbotInstance.UserData.fleets.Select(c => c.Ships).Sum(c => c.EspionageProbe);

									//Calculate best origin
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

									// If local record indicate not enough espionage probes are available, update record to make sure this is correct.
									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
										celestialProbes.Remove(bestOrigin.Origin.ID);
										celestialProbes.Add(bestOrigin.Origin.ID, tempCelestial.Ships.EspionageProbe);
									}

									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Insufficient probes ({celestialProbes[bestOrigin.Origin.ID]}/{neededProbes}).");
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildProbes") && _tbotInstance.InstanceSettings.AutoFarm.BuildProbes == true) {
											//Check if probes can be built
											var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Constructions);
											if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
												Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
												break;
											}

											tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Productions);
											if (tempCelestial.Productions.Any(p => p.ID == (int) Buildables.EspionageProbe)) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: Probes already building.");
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
													// celestialProbes must be refreshed here, or the check right after this block
													// (celestialProbes[...] >= neededProbes) still sees the pre-build count and
													// wrongly concludes there aren't enough probes even though the build just finished.
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
													stopAutoFarm = true;
													break;
												}
											}
										}
									}

									if (celestialProbes[bestOrigin.Origin.ID] >= neededProbes) {

										Ships ships = new();
										ships.Add(Buildables.EspionageProbe, neededProbes);

										// Use the fastest speed the origin's deuterium can actually afford - probes
										// have very little fuel margin, and at 100% speed a target a bit farther
										// than usual can be out of reach ("sondas não tem capacidade de combustível").
										var originForSpeed = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Resources);
										decimal spySpeed = _calculationService.CalcOptimalSpyProbeSpeed(originForSpeed.Coordinate, target.Celestial.Coordinate, ships, originForSpeed.Resources.Deuterium, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, originForSpeed.LFBonuses, _tbotInstance.UserData.userInfo.Class);

										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Spying {target.ToString()} from {bestOrigin.Origin.ToString()} with {neededProbes} probes at {spySpeed * 10}% speed.");

										var fleetId = (int) SendFleetCode.GenericError;
										int retryCount = 0;
										int maxRetryCount = 5;
										do {
											_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
											freeSlots = _tbotInstance.UserData.slots.Free;
											freeSlots = await WaitForFreeSlots(freeSlots, slotsToLeaveFree);
											fleetId = await _fleetScheduler.SendFleet(bestOrigin.Origin, ships, target.Celestial.Coordinate, Missions.Spy, spySpeed);
											if (fleetId == (int)SendFleetCode.NotEnoughSlots) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Another worker took the slot, waiting again for a free slot... Retry count: {retryCount}/{maxRetryCount}");
											}
											retryCount++;
										} while (fleetId == (int) SendFleetCode.NotEnoughSlots && retryCount <= maxRetryCount);

										if (fleetId > (int) SendFleetCode.GenericError) {
											freeSlots--;
											numProbed++;
											celestialProbes[bestOrigin.Origin.ID] -= neededProbes;

											if (target.State == FarmState.ProbesRequired || target.State == FarmState.FailedProbesRequired)
												continue;

											target.State = FarmState.ProbesSent;

											continue;
										} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
											stop = true;
											return;
										} else if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Unable to achieve a free slot after {retryCount} retries.");
											continue;
										} else {
											continue;
										}
									}
								}
							}
						}
					} catch (Exception e) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Exception: {e.Message}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to parse scan range");
					}

					// Wait for all espionage fleets to return.
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					Fleet firstReturning = _calculationService.GetLastReturningEspionage(_tbotInstance.UserData.fleets);
					if (firstReturning != null) {
						int interval = (int) ((1000 * firstReturning.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for all probes to return...");
						await Task.Delay(interval, _ct);
					}

					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Processing espionage reports of found inactives...");

					/// Process reports.
					await AutoFarmProcessReports();

					// P18 fix: re-spy targets left in ProbesRequired/FailedProbesRequired regardless of
					// where the ScanRange sweep currently is - see RespyPendingTargets() for why.
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
					freeSlots = await RespyPendingTargets(await GetCelestialProbes(), freeSlots, slotsToLeaveFree);

					bool probeUntilMinRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProbeUntilMinimumResources")
						&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ProbeUntilMinimumResources;
					if (probeUntilMinRes) {
						long totalLoot = _tbotInstance.UserData.farmTargets.Values
							.Where(t => t.State == FarmState.AttackPending && t.Report != null)
							.Sum(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources);
						long minTotal = (long) _tbotInstance.InstanceSettings.AutoFarm.MinimumResources;
						if (totalLoot < minTotal) {
							_waitingForLootThreshold = true;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								$"ProbeUntilMinimumResources: {totalLoot:N0}/{minTotal:N0} recursos acumulados, continuando no próximo ciclo...");
							return;
						}
						_waitingForLootThreshold = false;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ProbeUntilMinimumResources: meta atingida ({totalLoot:N0}/{minTotal:N0}), atacando...");
					}

					bool didStop;
					(didStop, freeSlots) = await AttackPendingTargets(freeSlots, slotsToLeaveFree, applyStopAfterScan: true);
					if (didStop) stop = true;
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarm Exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
			} finally {
				SaveManualActivitySeenState();
				if (_farmTargetCache != null)
					await _farmTargetCache.Save();
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacked targets: {_tbotInstance.UserData.farmTargets.Values.Where(t => t.State == FarmState.AttackSent).Count()}");
				//At this point no ProbesSent should remain in farmTargets
				foreach (var key in _tbotInstance.UserData.farmTargets.Where(kv => kv.Value.State == FarmState.ProbesSent).Select(kv => kv.Key).ToList())
					_tbotInstance.UserData.farmTargets.Remove(key);
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Stopping feature.");
						await EndExecution();
					} else {
						var time = await _tbotOgameBridge.GetDateTime();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						List<Fleet> orderedFleets = _tbotInstance.UserData.fleets
							.Where(fleet => fleet.Mission == Missions.Attack)
							.ToList();
						orderedFleets = orderedFleets
							.OrderByDescending(fleet => fleet.BackIn)
							.ToList();
						long interval;
						try {
							interval = (int) ((1000 * orderedFleets.First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds));
						} catch {
							interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMax);
							if (interval <= 0)
								interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						}
						var newTime = time.AddMilliseconds(interval);
						ChangeWorkerPeriod(interval);
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Next autofarm check at {newTime.ToString()}");
						await _tbotOgameBridge.CheckCelestials();
					}
				}
			}
		}

		/// <summary>
		/// Checks all received espionage reports and updates _tbotInstance.UserData.farmTargets to reflect latest data retrieved from reports.
		/// </summary>
		private async Task AutoFarmProcessReports() {
			// TODO Future: Read espionage reports in separate thread (concurently with probing itself).
			// TODO Future: Check if probes were destroyed, blacklist target if so to avoid additional kills.
			List<EspionageReportSummary> summaryReports = await _ogameService.GetEspionageReports();
			foreach (var summary in summaryReports) {
				DetectManualActivityReport(summary);

				if (summary.Type == EspionageReportType.Action)
					continue;

				try {
					var report = await _ogameService.GetEspionageReport(summary.ID);
					if (DateTime.Compare(report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), await _tbotOgameBridge.GetDateTime()) < 0) {
						await _ogameService.DeleteReport(report.ID);
						continue;
					}

					if (_tbotInstance.UserData.farmTargets.ContainsKey(FarmTarget.GetKey(report.Coordinate))) {
						FarmTarget target;
						_tbotInstance.UserData.farmTargets.TryGetValue(FarmTarget.GetKey(report.Coordinate), out var matchingTarget);
						if (matchingTarget == null) {
							// Report received of planet not in _tbotInstance.UserData.farmTargets. If inactive: add, otherwise: ignore.
							if (!report.IsInactive)
								continue;
							//Get corresponding planet. Add to target list.
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
							target = matchingTarget;
						}
						var newFarmTarget = target;

						if (target.State == FarmState.DefenseProbing) {
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						if (target.Report != null && DateTime.Compare(report.Date, target.Report.Date) < 0) {
							// Target has a more recent report. Delete report.
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						Buildables cargoShip;
						Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
						bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
						newFarmTarget.Report = report;
						// Keep the FastFarm cache up to date with whatever this report just confirmed,
						// regardless of the resulting state, so future FastFarmMode runs reflect it.
						await CacheUpsertFromReport(report);

						// Anti-Bashing: never attack a player who has already retaliated against us once,
						// no matter how good the loot looks - see PlayersDatabase/DefenderWorker.HandleAttack.
						if (_playersDatabase.IsBlacklisted(0, report.Username)) {
							newFarmTarget.State = FarmState.NotSuitable;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - player {report.Username} retaliated against us before (anti-bashing).");
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						// #10: poor-farm blacklist by player - recognizes a previously-blacklisted farmer
						// even if this report is for a different coordinate than the one that triggered it.
						if (_farmTargetCache.IsPlayerBlacklisted(report.Username, out DateTime playerBlacklistedUntil)) {
							if (DateTime.Now < playerBlacklistedUntil) {
								newFarmTarget.State = FarmState.NotSuitable;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - player {report.Username} is blacklisted until {playerBlacklistedUntil} (poor farm).");
								await _ogameService.DeleteReport(report.ID);
								continue;
							} else {
								_farmTargetCache.RemoveFromPlayerBlacklist(report.Username);
							}
						}

						// #19 (ideia vista no OgameBot): mesmo alvo marcado como inativo pelo jogo, uma
						// atividade recente no relatório é sinal de armadilha ou de que o jogador vai mover
						// os recursos antes da frota chegar - pular o ataque neste ciclo em vez de confiar
						// cegamente no rótulo IsInactive/IsLongInactive.
						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "SkipRecentlyActiveTargets") &&
							(bool) _tbotInstance.InstanceSettings.AutoFarm.SkipRecentlyActiveTargets &&
							report.LastActivity > 0 && report.LastActivity < 60) {
							newFarmTarget.State = FarmState.NotSuitable;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - recent activity detected ({report.LastActivity} min ago), skipping this cycle.");
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						bool probeUntilMinRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProbeUntilMinimumResources")
							&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ProbeUntilMinimumResources;
						if (probeUntilMinRes || MeetsLootThreshold(report.Loot(_tbotInstance.UserData.userInfo.Class))) {
							if (!report.HasFleetInformation || !report.HasDefensesInformation) {
								if (target.State == FarmState.ProbesRequired)
									newFarmTarget.State = FarmState.FailedProbesRequired;
								else if (target.State == FarmState.FailedProbesRequired)
									newFarmTarget.State = FarmState.NotSuitable;
								else
									newFarmTarget.State = FarmState.ProbesRequired;

								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Need more probes on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (report.IsDefenceless(isUsingProbes)) {
								newFarmTarget.State = FarmState.AttackPending;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (GetAcceptableFleetLossPercentage() > 0) {
								// Defenses/fleet present, but the user allows attacking through a garrison
								// within an acceptable fleet-loss threshold. The actual feasibility (does the
								// combat fleet available at the origin beat this target within the threshold?)
								// can only be checked once an origin/available-ships are picked, so this is
								// re-evaluated in AttackPendingTargets() right before sending.
								newFarmTarget.State = FarmState.AttackPending;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate} (defended - will simulate battle before sending). Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else {
								newFarmTarget.State = FarmState.NotSuitable;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - defences present.");
							}
						} else {
							newFarmTarget.State = FarmState.NotSuitable;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - insufficient loot ({report.Loot(_tbotInstance.UserData.userInfo.Class)})");
							try {
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
									SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
									(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active) {
									int resetHours = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours")
										? (int) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.ResetAfterHours : 168;
									long minRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist")
										? (long) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.MinimumResourcesToNotBlacklist : 0;
									if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minRes) {
										_farmTargetCache.Blacklist(report.Coordinate, DateTime.Now.AddHours(resetHours));
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Blacklisting {report.Coordinate} for {resetHours}h (loot below threshold).");
									}
								}
							} catch { }
						}

					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not scanned by TBot, ignoring...");
					}
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarmProcessReports Exception: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
					continue;
				}
			}

			await _ogameService.DeleteAllEspionageReports();

		}
	}
}
