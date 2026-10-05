using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Tbot.Common.Settings;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;

namespace TBot.WebUI.Services {
	public sealed class AutoFarmDashboardReader {
		private static readonly Regex SlotBudget = new(@"AutoFarm will use (?<available>\d+) slots \((?<used>\d+)/(?<max>\d+) already used\)", RegexOptions.Compiled);
		private static readonly Regex ExhaustedBudget = new(@"AutoFarm slot budget exhausted \((?<used>\d+)/(?<max>\d+)\)", RegexOptions.Compiled);
		private static readonly Regex CoordinatePattern = new(@"\[(?<type>[A-Za-z]+):(?<galaxy>\d+):(?<system>\d+):(?<position>\d+)\]", RegexOptions.Compiled);
		private static readonly Regex RequiredCargo = new(@"require (?<amount>[\d ]+) (?<ship>[A-Za-z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

		public async Task<AutoFarmDashboardStorage> Read(string instanceAlias) {
			var storage = new AutoFarmDashboardStorage();
			var settings = ReadSettings(instanceAlias);
			storage.ScanRanges.AddRange(settings.Ranges.Select(r => new AutoFarmScanRangeInfo {
				Galaxy = r.Galaxy,
				StartSystem = r.StartSystem,
				EndSystem = r.EndSystem
			}));
			var logs = ReadLogs();
			var autoFarmLogs = logs.Where(IsAutoFarmLog).ToList();

			storage.Logs.AddRange(ToLogRows(autoFarmLogs.TakeLast(120).Reverse()));
			storage.Errors.AddRange(ToLogRows(autoFarmLogs
				.Where(log => IsError(log) || log.Message.Contains("503", StringComparison.OrdinalIgnoreCase) || log.Message.Contains("302", StringComparison.OrdinalIgnoreCase))
				.TakeLast(40)
				.Reverse()));
		storage.Worker = BuildWorkerStatus(logs);
		storage.Slots = BuildSlotStatus(logs, settings.MaxSlots);
		storage.CombatResults.AddRange(ReadCombatResults(instanceAlias));

			// The log-line regex above only has data once AutoFarm has actually run a cycle and
			// logged its own slot-budget line - with Active off, or between cycles, usedSlots/
			// availableSlots stayed permanently null. Read the account's real fleet-slot usage
			// straight from ogamed instead, independent of TBot's own worker/Active state.
			var liveSlots = await ReadLiveSlots(settings.Host, settings.Port);
			if (liveSlots != null) {
				storage.Slots = new AutoFarmSlotStatus {
					MaxSlots = storage.Slots.MaxSlots ?? (int) liveSlots.Value.Total,
					UsedSlots = (int) liveSlots.Value.InUse,
					AvailableSlots = (int) (liveSlots.Value.Total - liveSlots.Value.InUse),
					ProbesInFlight = storage.Slots.ProbesInFlight,
					AttacksInFlight = storage.Slots.AttacksInFlight
				};
			}

			var databasePath = GetDatabasePath(instanceAlias);
			if (!File.Exists(databasePath)) {
				storage.Scan = BuildScanStatus(storage, settings, null, null);
				return storage;
			}

			try {
				using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
					DataSource = databasePath,
					Pooling = false
				}.ToString());
				connection.Open();

				if (HasTable(connection, "systems")) {
					ReadSystems(connection, storage, settings);
					ReadScanMap(connection, storage);
				}
				if (HasTable(connection, "targets"))
					ReadTargets(connection, storage, instanceAlias, logs);
				ReadAttacks(connection, storage);
				// Was summing storage.Reports - the CURRENT snapshot of the "targets" table (whatever
				// espionage reports happen to be cached right now, for targets in any state), not
				// actual attack results. Every cycle replaces/evicts rows there, so the "Gained (total)"
				// panel looked like it was constantly losing history even though nothing was actually
				// lost - it was never a total of gains to begin with. "attacks" (unlike "targets") is
				// insert-only, never pruned/deleted, so a real lifetime sum over ALL its rows (not just
				// the 100 read by ReadAttacks for the table view) is the genuinely eternal total.
				// Reported live 2026-09-18 ("Gained (total) deveria ser eterno, mas muito está sendo
				// perdido").
				storage.ResultsTotal = ReadAttacksLifetimeTotal(connection);
				var cursor = HasTable(connection, "scan_cursor") ? ReadCursor(connection) : null;

				storage.DatabaseAvailable = true;
				storage.Scan = BuildScanStatus(storage, settings, cursor, storage.LastSystemObservedAtUtc);
			} catch {
				storage.DatabaseAvailable = false;
			}

			return storage;
		}

		// Reads farm_results_{alias}.json, written by AutoFarmWorker's FeatureResultsStore -
		// same file/entry shape (Id/Coordinate/Summary/Resources/Ships/TimestampUtc) as the
		// expedition/discovery results stores, just mapped to the AutoFarm-specific row type.
		private static List<AutoFarmCombatResultRow> ReadCombatResults(string instanceAlias) {
			try {
				var path = Path.Combine(AppContext.BaseDirectory, "data", $"farm_results_{Path.GetFileName(instanceAlias)}.json");
				if (!File.Exists(path))
					return new List<AutoFarmCombatResultRow>();

				var entries = JArray.Parse(File.ReadAllText(path));
				return entries.OfType<JObject>()
					.Select(entry => new AutoFarmCombatResultRow {
						Coordinate = entry.Value<string>("Coordinate") ?? "",
						Summary = entry.Value<string>("Summary") ?? "",
						TimestampUtc = entry.Value<DateTime?>("TimestampUtc") ?? DateTime.MinValue
					})
					.OrderByDescending(row => row.TimestampUtc)
					.ToList();
			} catch {
				return new List<AutoFarmCombatResultRow>();
			}
		}

		private static void ReadSystems(SqliteConnection connection, AutoFarmDashboardStorage storage, DashboardSettings settings) {
			var hasPlanetsJson = HasColumn(connection, "systems", "planets_json");
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT galaxy, system, observed_at_utc, is_empty, " + (hasPlanetsJson ? "planets_json" : "NULL") + @"
FROM systems
ORDER BY observed_at_utc DESC
LIMIT 1000;";

			using var reader = command.ExecuteReader();
			while (reader.Read()) {
				var galaxy = reader.GetInt32(0);
				var system = reader.GetInt32(1);
				var observedAt = ParseDate(reader.GetString(2));
				var isEmpty = reader.GetInt64(3) != 0;
				var eligibleTargetCount = reader.IsDBNull(4) ? -1 : CountEligibleTargets(reader.GetString(4));
				var nextRefresh = observedAt.HasValue
					? observedAt.Value.Add(isEmpty
						? TimeSpan.FromDays(settings.EmptySystemCooldownDays)
						: TimeSpan.FromDays(settings.SystemDataDays))
					: (DateTime?)null;

				storage.Systems.Add(new AutoFarmSystemRow {
					Coordinate = $"{galaxy}:{system}",
					Status = isEmpty ? "Cached empty" : eligibleTargetCount == 0 ? "Cached no targets" : "Cached",
					Reason = isEmpty
						? "No planets or only vacation-mode inactives"
						: eligibleTargetCount == 0
							? "Planets exist, but none currently meet AutoFarm requirements"
							: "System data is reusable",
					LastScannedUtc = observedAt,
					NextRefreshUtc = nextRefresh
				});
			}

			storage.CachedSystemCount = storage.Systems.Count;
			var observedTimes = storage.Systems
				.Select(system => system.LastScannedUtc)
				.Where(value => value.HasValue)
				.Select(value => value!.Value)
				.ToList();
			storage.LastSystemObservedAtUtc = observedTimes.Count == 0 ? null : observedTimes.Max();
		}

		// Unlike ReadSystems above (LIMIT 1000, for the detailed table), this pulls every scanned
		// coordinate - light enough to cover the full ScanRange (up to ~2000 systems) in one query.
		// Also the authoritative source for CachedSystemCount/LastSystemObservedAtUtc/scan progress
		// below - ReadSystems' capped 1000-row list was silently pinning "Scan progress" at 50%
		// (1000/1996) forever once a ScanRange grew past 1000 systems, even after the real scan
		// reached 1995/1996 and completed (confirmed live 2026-09-04 by reading the database
		// directly: scan_cursor.full_scan_completed was already 1, but the dashboard still showed
		// stale 50% progress and "Not available" for cursor/last-observed).
		private static void ReadScanMap(SqliteConnection connection, AutoFarmDashboardStorage storage) {
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT galaxy, system, is_empty, observed_at_utc FROM systems;";
			using var reader = command.ExecuteReader();
			DateTime? lastObserved = null;
			while (reader.Read()) {
				var observedAt = ParseDate(reader.GetString(3));
				storage.ScanMap.Add(new AutoFarmScanMapCell {
					Galaxy = reader.GetInt32(0),
					System = reader.GetInt32(1),
					IsEmpty = reader.GetInt64(2) != 0
				});
				if (observedAt.HasValue && (lastObserved == null || observedAt.Value > lastObserved.Value))
					lastObserved = observedAt.Value;
			}
			storage.CachedSystemCount = storage.ScanMap.Count;
			storage.LastSystemObservedAtUtc = lastObserved;
		}

		private static int CountEligibleTargets(string json) {
			try {
				return JArray.Parse(json).OfType<JObject>().Count(planet =>
					planet.Value<bool?>("Inactive") == true &&
					planet.Value<bool?>("Administrator") != true &&
					planet.Value<bool?>("Banned") != true &&
					planet.Value<bool?>("Vacation") != true);
			} catch {
				return -1;
			}
		}

		private static void ReadTargets(SqliteConnection connection, AutoFarmDashboardStorage storage, string instanceAlias, List<LogRecord> logs) {
			var blacklist = ReadBlacklist(instanceAlias);
			var logReasons = BuildTargetReasons(logs);
			var consumedReportIds = ReadConsumedReportIds(connection);
			using var command = connection.CreateCommand();
			var hasConsumedReportColumn = HasColumn(connection, "targets", "consumed_report_id");
			command.CommandText = @"
SELECT name, state, galaxy, system, position, celestial_type, report_json,
  " + (hasConsumedReportColumn ? "consumed_report_id" : "NULL") + @", updated_at_utc
FROM targets
WHERE report_json IS NOT NULL
ORDER BY updated_at_utc DESC;";

			using var reader = command.ExecuteReader();
			while (reader.Read()) {
				var report = TryParseReport(reader.GetString(6));
				if (report == null)
					continue;

				var galaxy = reader.GetInt32(2);
				var system = reader.GetInt32(3);
				var position = reader.GetInt32(4);
				var coordinate = FormatCoordinate(galaxy, system, position, reader.GetInt32(5));
				var state = GetStateName(reader.GetInt32(1));
				var reportId = report.Value<int?>("ID") ?? 0;
				var consumedReportId = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
				var reason = ResolveTargetReason(state, coordinate, reportId, consumedReportId, consumedReportIds, blacklist, logReasons);

				storage.Reports.Add(new AutoFarmReportRow {
					TargetName = reader.GetString(0),
					State = state,
					Coordinate = coordinate,
					ReportId = reportId,
					ReportDateUtc = ParseDate(report.Value<string>("Date")),
					UpdatedAtUtc = ParseDate(reader.GetString(8)),
					Metal = report.Value<long?>("Metal") ?? 0,
					Crystal = report.Value<long?>("Crystal") ?? 0,
					Deuterium = report.Value<long?>("Deuterium") ?? 0,
					ConsumedReportId = consumedReportId,
					Reason = reason.Label,
					ReasonDetail = reason.Detail
				});
			}
		}

		private static HashSet<int> ReadConsumedReportIds(SqliteConnection connection) {
			var result = new HashSet<int>();
			if (!HasTable(connection, "consumed_reports"))
				return result;

			using var command = connection.CreateCommand();
			command.CommandText = "SELECT report_id FROM consumed_reports WHERE report_id > 0;";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				result.Add(reader.GetInt32(0));
			return result;
		}

		private static void ReadAttacks(SqliteConnection connection, AutoFarmDashboardStorage storage) {
			if (!HasTable(connection, "attacks"))
				return;

			var hasOrigin = HasColumn(connection, "attacks", "origin_galaxy") &&
				HasColumn(connection, "attacks", "origin_system") &&
				HasColumn(connection, "attacks", "origin_position") &&
				HasColumn(connection, "attacks", "origin_celestial_type");
			var hasMission = HasColumn(connection, "attacks", "mission");
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT report_id, galaxy, system, position, celestial_type, target_name,
  dispatched_at_utc, metal, crystal, deuterium, "
				+ (hasOrigin ? "origin_galaxy, origin_system, origin_position, origin_celestial_type" : "NULL, NULL, NULL, NULL")
				+ ", "
				+ (hasMission ? "mission" : "'Attack'")
				+ @"
FROM attacks
ORDER BY dispatched_at_utc DESC, report_id DESC
LIMIT 100;";

			using var reader = command.ExecuteReader();
			while (reader.Read()) {
				var origin = reader.IsDBNull(10) || reader.IsDBNull(11) || reader.IsDBNull(12) || reader.IsDBNull(13)
					? "Unknown origin"
					: FormatCoordinate(reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13));

				storage.Attacks.Add(new AutoFarmAttackRow {
					ReportId = reader.GetInt32(0),
					Coordinate = FormatCoordinate(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)),
					TargetName = reader.GetString(5),
					DispatchedAtUtc = ParseDate(reader.GetString(6)) ?? DateTime.MinValue,
					Metal = reader.GetInt64(7),
					Crystal = reader.GetInt64(8),
					Deuterium = reader.GetInt64(9),
					Origin = origin,
					Mission = reader.IsDBNull(14) ? "Attack" : reader.GetString(14)
				});
			}
		}

		// Real lifetime total, unlike ReadAttacks above (LIMIT 100, for the detailed table) - "attacks"
		// is insert-only (never pruned/deleted), so summing every row here is a genuinely eternal total.
		private static AutoFarmResultsTotal ReadAttacksLifetimeTotal(SqliteConnection connection) {
			if (!HasTable(connection, "attacks"))
				return new AutoFarmResultsTotal();

			using var command = connection.CreateCommand();
			command.CommandText = "SELECT SUM(metal), SUM(crystal), SUM(deuterium) FROM attacks;";
			using var reader = command.ExecuteReader();
			if (!reader.Read() || reader.IsDBNull(0))
				return new AutoFarmResultsTotal();

			return new AutoFarmResultsTotal {
				Metal = reader.GetInt64(0),
				Crystal = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
				Deuterium = reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
			};
		}

		private static AutoFarmScanCursorRow? ReadCursor(SqliteConnection connection) {
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT range_index, galaxy, system, updated_at_utc
FROM scan_cursor
WHERE id = 1;";
			using var reader = command.ExecuteReader();
			if (!reader.Read())
				return null;

			return new AutoFarmScanCursorRow(
				reader.GetInt32(0),
				reader.GetInt32(1),
				reader.GetInt32(2),
				ParseDate(reader.GetString(3)));
		}

		private static AutoFarmScanStatus BuildScanStatus(AutoFarmDashboardStorage storage, DashboardSettings settings, AutoFarmScanCursorRow? cursor, DateTime? lastObserved) {
			var scanned = storage.ScanMap.Count(cell =>
				settings.Ranges.Any(range => range.Galaxy == cell.Galaxy && cell.System >= range.StartSystem && cell.System <= range.EndSystem));
			var total = settings.Ranges.Sum(range => Math.Max(0, range.EndSystem - range.StartSystem + 1));
			return new AutoFarmScanStatus {
				CursorGalaxy = cursor?.Galaxy ?? 0,
				CursorSystem = cursor?.System ?? 0,
				CursorUpdatedAtUtc = cursor?.UpdatedAtUtc,
				CachedSystems = storage.CachedSystemCount,
				ScannedSystemsInRange = scanned,
				TotalSystemsInRange = total,
				ProgressPercent = total == 0 ? 0 : Math.Min(100, (int)Math.Round(scanned * 100d / total))
			};
		}

		private static AutoFarmWorkerStatus BuildWorkerStatus(List<LogRecord> logs) {
			var autoFarmLogs = logs.Where(log => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase)).ToList();
			var last = autoFarmLogs.LastOrDefault();
			var lastExecution = autoFarmLogs.LastOrDefault(log => log.Message.Contains("Running autofarm", StringComparison.OrdinalIgnoreCase));
			var next = autoFarmLogs.LastOrDefault(log => log.Message.Contains("Next autofarm check at", StringComparison.OrdinalIgnoreCase));
			DateTime? nextExecution = next == null ? null : ParseNextExecution(next.Message);

			var state = "Unknown";
			if (last != null) {
				if (last.Message.Contains("not enabled by settings", StringComparison.OrdinalIgnoreCase))
					state = "Disabled";
				else if (IsError(last))
					state = "Error";
				else if (DateTime.UtcNow - last.DateTimeUtc.ToUniversalTime() <= TimeSpan.FromMinutes(5))
					state = "Active";
				else
					state = "Stale";
			}

			return new AutoFarmWorkerStatus {
				State = state,
				LastExecutionUtc = lastExecution?.DateTimeUtc.ToUniversalTime(),
				LastActivityUtc = last?.DateTimeUtc.ToUniversalTime(),
				NextExecutionUtc = nextExecution?.ToUniversalTime(),
				NextExecutionText = nextExecution.HasValue ? nextExecution.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown"
			};
		}

		private static AutoFarmSlotStatus BuildSlotStatus(List<LogRecord> logs, int configuredMaxSlots) {
			var autoFarmLogs = logs.Where(log => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase)).ToList();
			int? maxSlots = configuredMaxSlots > 0 ? configuredMaxSlots : null;
			int? usedSlots = null;
			int? availableSlots = null;
			foreach (var log in autoFarmLogs) {
				var match = SlotBudget.Match(log.Message);
				if (match.Success) {
					availableSlots = int.Parse(match.Groups["available"].Value, CultureInfo.InvariantCulture);
					usedSlots = int.Parse(match.Groups["used"].Value, CultureInfo.InvariantCulture);
					maxSlots = int.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture);
					continue;
				}

				match = ExhaustedBudget.Match(log.Message);
				if (match.Success) {
					usedSlots = int.Parse(match.Groups["used"].Value, CultureInfo.InvariantCulture);
					maxSlots = int.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture);
					availableSlots = 0;
				}
			}

			var lastExecution = autoFarmLogs.LastOrDefault(log => log.Message.Contains("Running autofarm", StringComparison.OrdinalIgnoreCase));
			var afterExecution = lastExecution == null ? new List<LogRecord>() : logs.Where(log => log.DateTimeUtc >= lastExecution.DateTimeUtc).ToList();
			var latestReportProcessing = afterExecution.LastOrDefault(log => log.Message.Contains("Processing espionage reports", StringComparison.OrdinalIgnoreCase));
			var probes = afterExecution.Count(log => log.Sender.Equals("FleetScheduler", StringComparison.OrdinalIgnoreCase) && log.Message.Contains("Mission: Spy", StringComparison.OrdinalIgnoreCase));
			if (latestReportProcessing != null && latestReportProcessing.DateTimeUtc > (lastExecution?.DateTimeUtc ?? DateTime.MinValue))
				probes = 0;

			var attacks = afterExecution.Count(log => log.Sender.Equals("FleetScheduler", StringComparison.OrdinalIgnoreCase) && log.Message.Contains("Mission: Attack", StringComparison.OrdinalIgnoreCase));
			return new AutoFarmSlotStatus {
				MaxSlots = maxSlots,
				UsedSlots = usedSlots,
				AvailableSlots = availableSlots,
				ProbesInFlight = probes,
				AttacksInFlight = attacks
			};
		}

		private static List<LogRecord> ReadLogs() {
			var result = new List<LogRecord>();
			var logsPath = string.IsNullOrWhiteSpace(SettingsService.LogsPath)
				? Path.Combine(AppContext.BaseDirectory, "log")
				: SettingsService.LogsPath;
			var filePath = Path.Combine(logsPath, $"TBot{DateTime.Now:yyyyMMdd}.csv");
			if (!File.Exists(filePath))
				return result;

			try {
				using var file = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				using var stream = new StreamReader(file, Encoding.Default);
				using var csv = new CsvReader(stream, CultureInfo.InvariantCulture);
				foreach (var entry in csv.GetRecords<LogEntry>()) {
					if (!DateTime.TryParse(entry.datetime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var localTime))
						continue;
					result.Add(new LogRecord(entry.type ?? "", entry.sender ?? "", localTime, entry.message ?? ""));
				}
			} catch {
				return new List<LogRecord>();
			}

			return result.OrderBy(log => log.DateTimeUtc).ToList();
		}

		private static List<AutoFarmLogRow> ToLogRows(IEnumerable<LogRecord> logs) {
			return logs.Select(log => new AutoFarmLogRow {
				Level = log.Level,
				Sender = log.Sender,
				DateTimeUtc = log.DateTimeUtc.ToUniversalTime(),
				Message = log.Message,
				CopyText = $"[{log.DateTimeUtc:yyyy-MM-dd HH:mm:ss zzz}] [{log.Sender}] [{log.Level}] {log.Message}"
			}).ToList();
		}

		private static Dictionary<string, string> BuildTargetReasons(List<LogRecord> logs) {
			var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			string? currentAttackTarget = null;
			foreach (var log in logs.Where(log => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase))) {
				var coordinate = ExtractCoordinate(log.Message);
				if (log.Message.Contains("Attacking target", StringComparison.OrdinalIgnoreCase) && coordinate != null)
					currentAttackTarget = coordinate;

				if (currentAttackTarget != null && log.Message.Contains("Insufficient", StringComparison.OrdinalIgnoreCase)) {
					var cargo = RequiredCargo.Match(log.Message);
					reasons[currentAttackTarget] = cargo.Success
						? $"Blocked: {cargo.Groups["amount"].Value.Trim()} {cargo.Groups["ship"].Value} required"
						: "Blocked: insufficient fleet capacity";
				}

				if (log.Message.Contains("Ignoring previously handled non-actionable espionage report", StringComparison.OrdinalIgnoreCase) && coordinate != null)
					reasons[coordinate] = "Report already processed";

				if (coordinate != null && log.Message.Contains("No origin celestial available", StringComparison.OrdinalIgnoreCase))
					reasons[coordinate] = "No suitable origin";

				if (coordinate != null && log.Message.Contains("Attack dispatch failed", StringComparison.OrdinalIgnoreCase))
					reasons[coordinate] = "Attack dispatch failed";
			}
			return reasons;
		}

		private static (string Label, string Detail) ResolveTargetReason(string state, string coordinate, int reportId, int? consumedReportId, ISet<int> consumedReportIds, Dictionary<string, BlacklistEntry> blacklist, Dictionary<string, string> logReasons) {
			if (logReasons.TryGetValue(coordinate, out var loggedReason))
				return (loggedReason, "Taken from the latest AutoFarm log");
			if (state == "AttackSent")
				return ("Attack sent", "Fleet dispatch was recorded");
			if (blacklist.TryGetValue(coordinate, out var entry))
				return ($"Blacklisted until {entry.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}", $"Reason: {entry.Reason}");
			if (reportId > 0 && ((consumedReportId.HasValue && consumedReportId.Value == reportId) || consumedReportIds.Contains(reportId)))
				return ("Report already processed", $"Report #{reportId} is recorded as consumed");

			return state switch {
				"AttackPending" => ("Attack pending", "Waiting for a valid attack dispatch"),
				"NotSuitable" => ("Not suitable", "Report did not meet AutoFarm requirements"),
				"ProbesSent" => ("Waiting for report", "Espionage fleet is returning"),
				"ProbesRequired" => ("More probes required", "The report did not contain enough information"),
				"FailedProbesRequired" => ("More probes required", "The previous probe attempt was insufficient"),
				_ => (state, "No additional reason was recorded")
			};
		}

		private static Dictionary<string, BlacklistEntry> ReadBlacklist(string instanceAlias) {
			var result = new Dictionary<string, BlacklistEntry>(StringComparer.OrdinalIgnoreCase);
			var path = Path.Combine(AppContext.BaseDirectory, "data", $"autofarm_blacklist_{Path.GetFileName(instanceAlias)}.json");
			if (!File.Exists(path))
				return result;

			try {
				var entries = JArray.Parse(File.ReadAllText(path));
				foreach (var item in entries.OfType<JObject>()) {
					var coordinate = item["Coordinate"] as JObject;
					if (coordinate == null)
						continue;
					var galaxy = coordinate.Value<int?>("Galaxy");
					var system = coordinate.Value<int?>("System");
					var position = coordinate.Value<int?>("Position");
					var celestialType = coordinate.Value<int?>("Type") ?? coordinate.Value<int?>("CelestialType") ?? (int)Celestials.Planet;
					if (!galaxy.HasValue || !system.HasValue || !position.HasValue)
						continue;
					if (!DateTime.TryParse(item.Value<string>("ExpiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
						continue;
					result[FormatCoordinate(galaxy.Value, system.Value, position.Value, celestialType)] = new BlacklistEntry(item.Value<string>("Reason") ?? "Unknown", expiresAt);
				}
			} catch {
				return new Dictionary<string, BlacklistEntry>(StringComparer.OrdinalIgnoreCase);
			}
			return result;
		}

		private static DashboardSettings ReadSettings(string instanceAlias) {
			var settings = new DashboardSettings();
			try {
				var globalPath = SettingsService.GlobalSettingsPath;
				if (string.IsNullOrWhiteSpace(globalPath) || !File.Exists(globalPath))
					globalPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
				var global = JObject.Parse(File.ReadAllText(globalPath));
				var instancePath = ResolveInstanceSettingsPath(global, instanceAlias, Path.GetDirectoryName(globalPath));
				if (!File.Exists(instancePath))
					return settings;
				var instance = JObject.Parse(File.ReadAllText(instancePath));
				var general = instance["General"] as JObject;
				settings.Host = general?.Value<string>("Host");
				settings.Port = general?.Value<string>("Port");

				var autoFarm = instance["AutoFarm"] as JObject;
				if (autoFarm == null)
					return settings;

				settings.MaxSlots = autoFarm.Value<int?>("MaxSlots") ?? 0;
				settings.SystemDataDays = Math.Max(1, autoFarm.Value<int?>("DaysToKeepOldSystemData") ?? 7);
				settings.EmptySystemCooldownDays = Math.Max(1, autoFarm.Value<int?>("EmptySystemCooldownDays") ?? 30);
				foreach (var range in autoFarm["ScanRange"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) {
					var galaxy = range.Value<int?>("Galaxy");
					var start = range.Value<int?>("StartSystem");
					var end = range.Value<int?>("EndSystem");
					if (galaxy.HasValue && start.HasValue && end.HasValue)
						settings.Ranges.Add(new DashboardRange(galaxy.Value, Math.Min(start.Value, end.Value), Math.Max(start.Value, end.Value)));
				}
			} catch {
				return settings;
			}
			return settings;
		}

		private static string ResolveInstanceSettingsPath(JObject global, string instanceAlias, string? globalDirectory) {
			foreach (var instance in global["Instances"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) {
				if (!string.Equals(instance.Value<string>("Alias"), instanceAlias, StringComparison.OrdinalIgnoreCase))
					continue;
				var configured = instance.Value<string>("Settings");
				if (string.IsNullOrWhiteSpace(configured))
					break;
				return Path.IsPathRooted(configured) ? configured : Path.GetFullPath(Path.Combine(globalDirectory ?? AppContext.BaseDirectory, configured));
			}
			return Path.Combine(AppContext.BaseDirectory, "instance_settings.json");
		}

		private static DateTime? ParseNextExecution(string message) {
			const string marker = "Next autofarm check at ";
			var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (start < 0)
				return null;
			start += marker.Length;
			var end = message.IndexOf(" (", start, StringComparison.Ordinal);
			var value = end > start ? message[start..end] : message[start..];
			return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
		}

		private static bool IsError(LogRecord log) => log.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) || log.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || log.Message.Contains("Exception", StringComparison.OrdinalIgnoreCase);

		private static bool IsAutoFarmLog(LogRecord log) => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase) && !IsAdminLifecycleLine(log.Message);

		// WorkerBase's own Starting/Restarting/Closing/"not enabled by settings" lines (see
		// WorkerBase.cs's ShouldLogAdminLines) are worker plumbing, not AutoFarm decisions - the
		// Worker status panel already summarizes that state, so repeating it in the log/error feed
		// just buries the handful of substantive lines a disabled or idle instance actually has.
		private static bool IsAdminLifecycleLine(string message) {
			// Contains, not StartsWith/EndsWith: the stored message already carries the
			// "[Player@Server] " account prefix ahead of it (confirmed live 2026-08-29 - the
			// StartsWith checks never matched anything because of that prefix, so this filter
			// was silently a no-op).
			return message.Contains("Starting Worker \"", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("Restarting Worker \"", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("Closing Worker \"", StringComparison.OrdinalIgnoreCase)
				|| (message.Contains("Worker \"", StringComparison.OrdinalIgnoreCase) && message.Contains("closed!", StringComparison.OrdinalIgnoreCase))
				|| message.Contains("not enabled by settings", StringComparison.OrdinalIgnoreCase);
		}

		private static string? ExtractCoordinate(string message) {
			var match = CoordinatePattern.Match(message ?? string.Empty);
			return match.Success ? $"[{NormalizeCoordinateType(match.Groups["type"].Value)}:{match.Groups["galaxy"].Value}:{match.Groups["system"].Value}:{match.Groups["position"].Value}]" : null;
		}

		private static string NormalizeCoordinateType(string type) => type.Equals("M", StringComparison.OrdinalIgnoreCase) ? "M" : "P";

		private static JObject? TryParseReport(string json) {
			try { return JObject.Parse(json); } catch { return null; }
		}

		private static DateTime? ParseDate(string? value) {
			return string.IsNullOrWhiteSpace(value) || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
				? null
				: parsed.ToUniversalTime();
		}

		private static string GetStateName(int state) => Enum.IsDefined(typeof(FarmState), state) ? ((FarmState)state).ToString() : "Unknown";

		private static string FormatCoordinate(int galaxy, int system, int position, int celestialType) {
			var code = (Celestials)celestialType == Celestials.Moon ? "M" : "P";
			return $"[{code}:{galaxy}:{system}:{position}]";
		}

		private static string GetDatabasePath(string instanceAlias) {
			return Path.Combine(AppContext.BaseDirectory, "data", Path.GetFileName($"autofarm_{instanceAlias}.db"));
		}

		private static async Task<(long InUse, long Total)?> ReadLiveSlots(string? host, string? port) {
			if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port))
				return null;

			try {
				var response = await Http.GetAsync($"http://{host}:{port}/bot/fleets/slots");
				if (!response.IsSuccessStatusCode)
					return null;

				var envelope = JObject.Parse(await response.Content.ReadAsStringAsync());
				var result = (envelope["Result"] as JObject) ?? (envelope["result"] as JObject);
				if (result == null)
					return null;

				return (result.Value<long?>("InUse") ?? 0, result.Value<long?>("Total") ?? 0);
			} catch {
				return null;
			}
		}

		private static bool HasTable(SqliteConnection connection, string tableName) {
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
			command.Parameters.AddWithValue("$name", tableName);
			return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
		}

		private static bool HasColumn(SqliteConnection connection, string tableName, string columnName) {
			using var command = connection.CreateCommand();
			command.CommandText = $"PRAGMA table_info([{tableName}]);";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		private sealed class LogEntry {
			public string type { get; set; } = string.Empty;
			public string sender { get; set; } = string.Empty;
			public string datetime { get; set; } = string.Empty;
			public string message { get; set; } = string.Empty;
		}

		private sealed record LogRecord(string Level, string Sender, DateTime DateTimeUtc, string Message);
		private sealed record BlacklistEntry(string Reason, DateTime ExpiresAtUtc);
		private sealed record AutoFarmScanCursorRow(int RangeIndex, int Galaxy, int System, DateTime? UpdatedAtUtc);
		private sealed record DashboardRange(int Galaxy, int StartSystem, int EndSystem);

		private sealed class DashboardSettings {
			public int MaxSlots { get; set; }
			public int SystemDataDays { get; set; } = 7;
			public int EmptySystemCooldownDays { get; set; } = 30;
			public List<DashboardRange> Ranges { get; } = new();
			public string? Host { get; set; }
			public string? Port { get; set; }
		}
	}

	public sealed class AutoFarmDashboardStorage {
		public bool DatabaseAvailable { get; set; }
		public int CachedSystemCount { get; set; }
		public DateTime? LastSystemObservedAtUtc { get; set; }
		public AutoFarmWorkerStatus Worker { get; set; } = new();
		public AutoFarmSlotStatus Slots { get; set; } = new();
		public AutoFarmScanStatus Scan { get; set; } = new();
		public List<AutoFarmSystemRow> Systems { get; } = new();
		public List<AutoFarmScanMapCell> ScanMap { get; } = new();
		public List<AutoFarmScanRangeInfo> ScanRanges { get; } = new();
		public List<AutoFarmReportRow> Reports { get; } = new();
		public List<AutoFarmAttackRow> Attacks { get; } = new();
		public List<AutoFarmLogRow> Logs { get; } = new();
		public List<AutoFarmLogRow> Errors { get; } = new();
		public List<AutoFarmCombatResultRow> CombatResults { get; } = new();
		public AutoFarmResultsTotal ResultsTotal { get; set; } = new();
	}
}
