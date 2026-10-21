using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
	public class ExpeditionsWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		private int _nextOriginIndex;

		// Expedition results only exist in the game's own message list, so the message IDs already logged are tracked to avoid re-logging them every cycle.
		private readonly HashSet<int> _loggedExpeditionMessageIds = new();
		private readonly FeatureResultsStore _expeditionResultsStore;
		private readonly FeatureResultsStore _discoveryResultsStore;
		// Permanent counterparts of the capped JSON stores, which the user does not want limited to 200 entries.
		private readonly FeatureResultsSqliteStore _expeditionResultsPermanentStore;
		private readonly FeatureResultsSqliteStore _discoveryResultsPermanentStore;

		public ExpeditionsWorker(
			ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) : base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
			_expeditionResultsStore = new FeatureResultsStore($"expedition_results_{parentInstance.InstanceAlias}.json");
			_discoveryResultsStore = new FeatureResultsStore($"discovery_results_{parentInstance.InstanceAlias}.json");
			_expeditionResultsPermanentStore = new FeatureResultsSqliteStore($"expedition_results_{parentInstance.InstanceAlias}.db");
			_discoveryResultsPermanentStore = new FeatureResultsSqliteStore($"discovery_results_{parentInstance.InstanceAlias}.db");

			// One-time backfill of the capped JSON stores into the permanent SQLite ones; idempotent, so it can run on every startup.
			foreach (var entry in _expeditionResultsStore.GetAll())
				_expeditionResultsPermanentStore.Add(entry);
			foreach (var entry in _discoveryResultsStore.GetAll())
				_discoveryResultsPermanentStore.Add(entry);
		}

		// Reverted 2026-09-02 per explicit user request: Active:false now stops this worker
		// completely again (no background result-polling/log noise), same as every other worker.
		// Result polling moved to OnDisabledTick so expedition/discovery results (artifacts, lifeform
		// XP, etc.) are still persisted to the database while Active is off - only the logging of
		// "Recorded N new result(s)" is suppressed in that case to avoid log spam.
		public override bool IsWorkerEnabledBySettings() => (bool) _tbotInstance.InstanceSettings.Expeditions.Active;

		public override string GetWorkerName() => "Expeditions";
		public override Feature GetFeature() => Feature.Expeditions;
		public override LogSender GetLogSender() => LogSender.Expeditions;

		protected override async Task OnDisabledTick() {
			// Results are still collected while disabled so the dashboards keep their lifetime totals; logging is suppressed to avoid spam.
			await LogNewExpeditionResults(suppressLogging: true);
		}

		private int CountActiveExpeditionsFromOrigin(Celestial origin) {
			if (origin?.Coordinate == null || _tbotInstance.UserData.fleets == null)
				return 0;

			return _tbotInstance.UserData.fleets.Count(f =>
				f.Mission == Missions.Expedition &&
				f.Origin != null &&
				f.Origin.Galaxy == origin.Coordinate.Galaxy &&
				f.Origin.System == origin.Coordinate.System &&
				f.Origin.Position == origin.Coordinate.Position &&
				f.Origin.Type == origin.Coordinate.Type);
		}

		private List<Celestial> ResolveExpeditionOrigins() {
			var available = (_tbotInstance.UserData.celestials ?? new List<Celestial>())
				.Where(c => c?.Coordinate != null)
				.ToList();
			var origins = new List<Celestial>();

			if (_tbotInstance.InstanceSettings.Expeditions.Origin.Length > 0) {
				foreach (var configured in _tbotInstance.InstanceSettings.Expeditions.Origin) {
					try {
						var type = Enum.Parse<Celestials>(configured.Type.ToString(), true);
						var match = available.FirstOrDefault(c =>
							c.Coordinate.Galaxy == (int)configured.Galaxy &&
							c.Coordinate.System == (int)configured.System &&
							c.Coordinate.Position == (int)configured.Position &&
							c.Coordinate.Type == type);

						if (match == null) {
							DoLog(LogLevel.Warning,
								$"Expedition origin not found: {configured.Galaxy}:{configured.System}:{configured.Position} ({type})");
							continue;
						}

						if (!origins.Any(o => o.Coordinate.IsSame(match.Coordinate)))
							origins.Add(match);
					} catch (Exception e) {
						DoLog(LogLevel.Warning, $"Unable to parse expedition origin: {e.Message}");
					}
				}
			} else {
				// Automatic mode chooses one origin from the cached snapshot. It does not
				// refresh every celestial just to compare them.
				var best = available
					.OrderBy(c => c.Coordinate.Type == Celestials.Moon)
					.ThenByDescending(c => c.Ships == null ? 0 : _calculationService.CalcFleetCapacity(
						c.Ships,
						_tbotInstance.UserData.serverData,
						_tbotInstance.UserData.researches.HyperspaceTechnology,
						null,
						_tbotInstance.UserData.userInfo.Class,
						_tbotInstance.UserData.serverData.ProbeCargo))
					.FirstOrDefault();
				if (best != null)
					origins.Add(best);
			}

			return (bool)_tbotInstance.InstanceSettings.Expeditions.RandomizeOrder
				? origins.Shuffle().ToList()
				: origins;
		}

		private Ships BuildManualExpeditionFleet() {
			return new Ships(
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "LightFighter", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "HeavyFighter", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Cruiser", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Battleship", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Battlecruiser", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Bomber", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Destroyer", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Deathstar", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "SmallCargo", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "LargeCargo", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "ColonyShip", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Recycler", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "EspionageProbe", 0L),
				0,
				0,
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Reaper", 0L),
				(long) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships, "Pathfinder", 0L));
		}

		private Ships BuildExpeditionFleet(Celestial origin, LFBonuses lfBonuses, int expeditionsRemainingFromOrigin) {
			if ((bool)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Active)
				return BuildManualExpeditionFleet();

			Buildables primaryShip = Buildables.LargeCargo;
			if (!Enum.TryParse<Buildables>(_tbotInstance.InstanceSettings.Expeditions.PrimaryShip.ToString(), true, out primaryShip)) {
				DoLog(LogLevel.Warning, "Unable to parse PrimaryShip. Using LargeCargo.");
				primaryShip = Buildables.LargeCargo;
			}
			if (primaryShip == Buildables.Null)
				return new Ships();

			var availableShips = origin.Ships?.GetMovableShips() ?? new Ships();
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "PrimaryToKeep") &&
				(int)_tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep > 0) {
				availableShips.SetAmount(primaryShip, Math.Max(0,
					availableShips.GetAmount(primaryShip) - (long)_tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep));
			}

			// The number of expeditions still queued for this origin is passed in, so a scarce fleet is split between them instead of given whole to the first one.
			int expeditionsNumber = Math.Max(1, expeditionsRemainingFromOrigin);

			return _calculationService.CalcFullExpeditionShips(
				availableShips,
				primaryShip,
				expeditionsNumber,
				_tbotInstance.UserData.serverData,
				_tbotInstance.UserData.researches,
				lfBonuses,
				_tbotInstance.UserData.userInfo.Class,
				_tbotInstance.UserData.serverData.ProbeCargo);
		}

		private async Task<Ships> RefreshExpeditionShips(Celestial origin) {
			try {
				// The bridge deliberately swallows update errors for the other workers. That
				// is useful for a best-effort empire refresh, but it is unsafe here: a stale
				// empty snapshot would make a valid origin look permanently unusable.
				origin.Ships = await _ogameService.GetShips(origin);
				return origin.Ships;
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"Unable to refresh expedition ships on {origin.Coordinate}: {e.Message}");
				return null;
			}
		}

		private Coordinate BuildExpeditionDestination(Celestial origin, int expeditionCount, List<int> usedSystems, Random random) {
			if (!(bool)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Active) {
				return new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = origin.Coordinate.System,
					Position = 16,
					Type = Celestials.DeepSpace
				};
			}

			int range = Math.Max(1, (int)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Range);
			while (expeditionCount > range * 2)
				range++;

			for (int attempt = 0; attempt < 100; attempt++) {
				int system = GeneralHelper.WrapSystem(random.Next(
					origin.Coordinate.System - range,
					origin.Coordinate.System + range + 1));
				if (usedSystems.Contains(system))
					continue;

				usedSystems.Add(system);
				return new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = system,
					Position = 16,
					Type = Celestials.DeepSpace
				};
			}

			return new Coordinate {
				Galaxy = origin.Coordinate.Galaxy,
				System = origin.Coordinate.System,
				Position = 16,
				Type = Celestials.DeepSpace
			};
		}

		// The message content is raw game HTML, so tags and entities are stripped down to plain narrative text before it reaches the log.
		private static string PlainTextFromMessageContent(string content) {
			if (string.IsNullOrEmpty(content))
				return "";

			var withoutBreaks = Regex.Replace(content, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
			var withoutTags = Regex.Replace(withoutBreaks, @"<[^>]+>", "");
			var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
			return Regex.Replace(decoded, @"\s+", " ").Trim();
		}

		private async Task LogNewExpeditionResults(bool suppressLogging = false) {
			try {
				var messages = await _ogameService.GetExpeditionMessages();
				if (messages == null)
					return;

				const long DiscoveryGlobalTypeID = 61;
				int newExpeditions = 0, newDiscoveries = 0;

				foreach (var message in messages.OrderBy(m => m.CreatedAt)) {
					if (!_loggedExpeditionMessageIds.Add(message.ID))
						continue;

					// Expeditions and AutoDiscovery share the same in-game message tab, so each message is routed to its own structured results store instead of the worker log.
					bool isDiscovery = message.GlobalTypeID == DiscoveryGlobalTypeID;
					var store = isDiscovery ? _discoveryResultsStore : _expeditionResultsStore;
					string summary = PlainTextFromMessageContent(message.Content);
					// Artifact finds carry no Resources/Ships, so they are surfaced explicitly instead of being buried in the free-text summary.
					string artifactsOrLifeform = "";
					if (isDiscovery && message.ArtifactsFound > 0) {
						artifactsOrLifeform = $"{message.ArtifactsFound} artifacts ({message.ArtifactsSize})";
						summary = $"Artifacts found: {message.ArtifactsFound} ({message.ArtifactsSize}). {summary}";
					} else if (isDiscovery && message.DiscoveryType == "lifeform-xp") {
						// Confirmed via HAR capture 2026-08-29: this discovery subtype carries no
						// Resources/Ships either, same gap as the artifacts case above.
						string ownedNote = message.LifeformAlreadyOwned ? "already known" : "new";
						artifactsOrLifeform = $"{message.LifeformGainedExperience} XP ({message.LifeformDiscovered}, {ownedNote})";
						summary = $"Lifeform XP gained: {message.LifeformGainedExperience} ({message.LifeformDiscovered}, {ownedNote}). {summary}";
					}
					var resultEntry = new FeatureResultEntry {
						Id = message.ID,
						Coordinate = message.Coordinate?.ToString() ?? "",
						Summary = summary,
						Resources = message.Resources != null && message.Resources.TotalResources > 0 ? message.Resources.ToString() : "",
						Ships = message.Ships != null && !message.Ships.IsEmpty() ? message.Ships.ToString() : "",
						TimestampUtc = message.CreatedAt,
						Metal = message.Resources?.Metal ?? 0,
						Crystal = message.Resources?.Crystal ?? 0,
						Deuterium = message.Resources?.Deuterium ?? 0,
						Darkmatter = message.Resources?.Darkmatter ?? 0,
						ArtifactsOrLifeform = artifactsOrLifeform,
						ArtifactsFound = message.ArtifactsFound
					};
					store.Add(resultEntry);
					// Permanent, unbounded copy - see FeatureResultsSqliteStore for why this exists
					// alongside the capped JSON store above.
					(isDiscovery ? _discoveryResultsPermanentStore : _expeditionResultsPermanentStore).Add(resultEntry);

					if (isDiscovery) newDiscoveries++; else newExpeditions++;
				}

if (!suppressLogging) {
				if (newExpeditions > 0)
					DoLog(LogLevel.Information, $"Recorded {newExpeditions} new expedition result(s).");
				if (newDiscoveries > 0)
					_tbotInstance.log(LogLevel.Information, LogSender.AutoDiscovery, $"Recorded {newDiscoveries} new discovery result(s).");
			}
			} catch (Exception e) {
				// Best-effort reporting, not core to sending expeditions - a failure here shouldn't
				// stop the worker from planning and dispatching this cycle's fleets.
				DoLog(LogLevel.Debug, $"Unable to read expedition messages: {e.Message}");
			}
		}

		protected override async Task Execute() {
			bool stop = false;
			bool delay = false;

			try {
				await LogNewExpeditionResults();

				_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
				if (_tbotInstance.UserData.researches.Astrophysics == 0) {
					DoLog(LogLevel.Information, "Skipping: Astrophysics not yet researched!");
					ChangeWorkerPeriod(RandomizeHelper.CalcRandomInterval(IntervalType.AboutHalfAnHour));
					return;
				}

				// This is the only account-wide snapshot phase. No origin is selected here.
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				_tbotInstance.UserData.serverData = await _ogameService.GetServerData();

				var rankSlotsPriority = new List<RankSlotsPriority> {
					new RankSlotsPriority(Feature.BrainAutoMine,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
						(bool)_tbotInstance.InstanceSettings.Brain.Active && (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active &&
						((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active),
						(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
					new RankSlotsPriority(Feature.Expeditions,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
						true,
						(int)_tbotInstance.UserData.slots.ExpTotal,
						(int)_tbotInstance.UserData.slots.ExpInUse),
					new RankSlotsPriority(Feature.AutoFarm,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
						(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
						(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)),
					new RankSlotsPriority(Feature.Colonize,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ? (int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots : 1,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
					new RankSlotsPriority(Feature.AutoDiscovery,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
						(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
						(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
					new RankSlotsPriority(Feature.Harvest,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
						(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
						(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
				};

				int maxSlots = Math.Max(0,
					_tbotInstance.UserData.slots.Total - (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree);
				int expeditionsToSend = Math.Min(
					Math.Min(_tbotInstance.UserData.slots.ExpFree, _tbotInstance.UserData.slots.Free),
					maxSlots);

				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForAllExpeditions") &&
					(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions &&
					_tbotInstance.UserData.slots.ExpInUse > 0)
					expeditionsToSend = 0;

				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForMajorityOfExpeditions") &&
					(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions &&
					(double)expeditionsToSend < Math.Round((double)_tbotInstance.UserData.slots.ExpTotal / 2D, 0, MidpointRounding.ToZero) + 1D)
					expeditionsToSend = 0;

				if (expeditionsToSend > 0) {
					int prioritySlots = _calculationService.CalcSlotsPriority(
						Feature.Expeditions,
						rankSlotsPriority,
						_tbotInstance.UserData.slots,
						_tbotInstance.UserData.fleets,
						(int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree);
					expeditionsToSend = Math.Min(expeditionsToSend, Math.Max(0, prioritySlots));
				}

				if (expeditionsToSend <= 0) {
					delay = true;
					return;
				}

				var origins = ResolveExpeditionOrigins();
				if (origins.Count == 0) {
					DoLog(LogLevel.Warning, "No valid expedition origin is configured.");
					delay = true;
					return;
				}

				// LF bonuses are account-wide. They must not be refreshed once per origin.
				var lfBonuses = await _ogameService.GetLFBonuses();
				int maxPerOrigin = (int?)_tbotInstance.InstanceSettings.Expeditions.MaxExpeditionsPerOrigin ?? 1;
				var capacities = origins
					.Select(origin => Math.Max(0, maxPerOrigin - CountActiveExpeditionsFromOrigin(origin)))
					.ToArray();
				var sentByOrigin = new int[origins.Count];
				var unavailableOrigins = new bool[origins.Count];
				var retryableUnavailableOrigins = new bool[origins.Count];
				var retriedUnavailableOrigins = false;
				var remaining = expeditionsToSend;
				var planningStart = _nextOriginIndex % origins.Count;
				var random = new Random();
				var usedSystems = new List<int>();

			while (remaining > 0) {
				// Rebuild the plan after every failed origin. This lets an origin with
				// spare capacity take over instead of losing a slot.
				var residualCapacities = capacities
					.Select((capacity, index) => unavailableOrigins[index]
						? 0
						: Math.Max(0, capacity - sentByOrigin[index]))
					.ToArray();
				var plan = ExpeditionOriginPlanner.BuildRoundRobinPlan(residualCapacities, remaining, planningStart);
				if (plan.Sum() == 0) {
					// A failed ship refresh is not a permanent property of an origin. Ships
					// may have returned while this cycle was running. Re-open the origins
					// once when the first pass cannot satisfy the remaining demand; this is
					// intentionally bounded so a permanently invalid origin is not polled.
					if (ExpeditionOriginPlanner.ShouldRetryUnavailableOrigins(
						remaining, retryableUnavailableOrigins, retriedUnavailableOrigins)) {
						retriedUnavailableOrigins = true;
						for (var index = 0; index < unavailableOrigins.Length; index++)
							unavailableOrigins[index] = unavailableOrigins[index] && !retryableUnavailableOrigins[index];
						DoLog(LogLevel.Information,
							$"Expedition origin capacity is still short by {remaining}; refreshing previously unavailable origins once.");
						continue;
					}
					break;
				}

				var progressInPass = false;
				for (var offset = 0; offset < origins.Count && remaining > 0; offset++) {
					var originIndex = (planningStart + offset) % origins.Count;
					var count = plan[originIndex];
					if (count <= 0)
						continue;

					var origin = origins[originIndex];
					for (var attempt = 0; attempt < count && remaining > 0; attempt++) {
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						if (CountActiveExpeditionsFromOrigin(origin) >= maxPerOrigin)
							break;

						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						if (_tbotInstance.UserData.slots.ExpFree <= 0 ||
							_tbotInstance.UserData.slots.Free <= (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
							delay = true;
							return;
						}

						var refreshedShips = await RefreshExpeditionShips(origin);
						if (refreshedShips == null) {
							unavailableOrigins[originIndex] = true;
							retryableUnavailableOrigins[originIndex] = true;
							break;
						}

						var fleet = BuildExpeditionFleet(origin, lfBonuses, count - attempt);
						if (fleet == null || fleet.IsEmpty() || !refreshedShips.HasAtLeast(fleet, 1)) {
							DoLog(LogLevel.Information,
								$"No usable expedition fleet on {origin.Coordinate}; excluding this origin until the bounded retry.");
							unavailableOrigins[originIndex] = true;
							retryableUnavailableOrigins[originIndex] = true;
							break;
						}

						var destination = BuildExpeditionDestination(origin, remaining, usedSystems, random);
						var payload = new Resources();
						if ((long)_tbotInstance.InstanceSettings.Expeditions.FuelToCarry > 0)
							payload.Deuterium = (long)_tbotInstance.InstanceSettings.Expeditions.FuelToCarry;

						DoLog(LogLevel.Information, $"Sending expedition from {origin.Coordinate} to {destination}");
					var fleetId = await _fleetScheduler.SendFleet(
						origin,
						fleet,
						destination,
						Missions.Expedition,
						Speeds.HundredPercent,
						payload,
						_tbotInstance.UserData.userInfo.Class,
						false,
						true);

						if (fleetId == (int)SendFleetCode.AfterSleepTime) {
							stop = true;
							return;
						}
						if (fleetId == (int)SendFleetCode.NotEnoughSlots) {
							delay = true;
							return;
						}
						if (fleetId <= (int)SendFleetCode.GenericError) {
							DoLog(LogLevel.Warning, $"Expedition was not sent from {origin.Coordinate}; excluding this origin for the rest of the cycle.");
							unavailableOrigins[originIndex] = true;
							break;
						}

						sentByOrigin[originIndex]++;
						remaining--;
						progressInPass = true;
						planningStart = (originIndex + 1) % origins.Count;

						int minWait = (int)_tbotInstance.InstanceSettings.Expeditions.MinWaitNextFleet;
						int maxWait = (int)_tbotInstance.InstanceSettings.Expeditions.MaxWaitNextFleet;
						if (maxWait < minWait)
							(minWait, maxWait) = (maxWait, minWait);
						await Task.Delay((int)RandomizeHelper.CalcRandomIntervalSecToMs(minWait, maxWait), _ct);
					}
				}

				if (!progressInPass) {
					if (ExpeditionOriginPlanner.ShouldRetryUnavailableOrigins(
						remaining, retryableUnavailableOrigins, retriedUnavailableOrigins)) {
						retriedUnavailableOrigins = true;
						for (var index = 0; index < unavailableOrigins.Length; index++)
							unavailableOrigins[index] = unavailableOrigins[index] && !retryableUnavailableOrigins[index];
						DoLog(LogLevel.Information,
							$"No expedition was dispatched in the pass; refreshing previously unavailable origins once.");
						continue;
					}
					break;
				}
			}

			_nextOriginIndex = planningStart;

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				var expeditionFleets = _tbotInstance.UserData.fleets
					.Where(f => f.Mission == Missions.Expedition)
					.OrderBy(f => f.BackIn)
					.ToList();
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();

				long interval;
				if (expeditionFleets.Count == 0 ||
					(_tbotInstance.UserData.slots.ExpFree > 0 &&
					 !(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions &&
					 !(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions)) {
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.AboutFiveMinutes);
				} else {
					interval = (int)((1000 * expeditionFleets.First().BackIn) +
						RandomizeHelper.CalcRandomIntervalSecToMs(
							(int)_tbotInstance.InstanceSettings.Expeditions.MinWaitNextRound,
							(int)_tbotInstance.InstanceSettings.Expeditions.MaxWaitNextRound));
				}

				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next expedition check at {DateTime.Now.AddMilliseconds(interval)}");
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"HandleExpeditions exception: {e.Message}");
				DoLog(LogLevel.Debug, e.StackTrace);
				ChangeWorkerPeriod(RandomizeHelper.CalcRandomInterval(IntervalType.AMinuteOrTwo));
			} finally {
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop)
						await EndExecution();

					if (delay) {
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						long interval;
						try {
							var returningFleets = _tbotInstance.UserData.fleets.Where(f => f.BackIn.HasValue).ToList();
							interval = returningFleets.Any()
								? (returningFleets.Min(f => f.BackIn.Value) * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds)
								: RandomizeHelper.CalcRandomInterval(
									(int) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions, "CheckIntervalMin", 30),
									(int) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions, "CheckIntervalMax", 60));
						} catch {
							interval = RandomizeHelper.CalcRandomInterval(
								(int) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions, "CheckIntervalMin", 30),
								(int) SettingsService.GetSetting(_tbotInstance.InstanceSettings.Expeditions, "CheckIntervalMax", 60));
						}
						ChangeWorkerPeriod(interval);
					}

					await _tbotOgameBridge.CheckCelestials();
				}
			}
		}
	}
}
