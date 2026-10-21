using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Tbot.Common.Settings;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;

namespace TBot.WebUI.Services {
	// Lighter counterpart to AutoFarmDashboardReader: workers without a rich SQLite store expose only status + recent logs.
	public sealed class FeatureDashboardConfig {
		public string Sender { get; init; } = string.Empty;
		public string[] NextCheckMarkers { get; init; } = Array.Empty<string>();
		public string DispatchMarker { get; init; } = string.Empty;
		public string? MaxSlotsSettingSection { get; init; }
		// Defaults to "MaxSlots"; Expeditions has no such field and uses the live expedition pool instead.
		public string MaxSlotsSettingKey { get; init; } = "MaxSlots";
		// ogamed serializes Fleet.Mission as a plain number (Go's MissionID has no JSON marshaler), matching the Missions enum values; null skips the live-fleet section.
		public Missions? LiveFleetsMission { get; init; }
		// Expeditions' cap is the account's live expedition slot pool (ExpTotal), not a static setting; when true, MaxSlots is read from there.
		public bool LiveMaxSlotsFromExpeditionPool { get; init; }
		// File name (in the instance's data/ folder) written by TBot's own FeatureResultsStore -
		// structured results instead of scraping them back out of the log.
		public string? ResultsFileName { get; init; }
		// When true, the Results table shows an Artifacts/Lifeform column instead of Resources/Ships.
		public bool ResultsShowArtifactsColumn { get; init; }
	}

	public sealed class FeatureDashboardReader {
		private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

		public async Task<FeatureDashboardModel> Read(FeatureDashboardConfig config, string instanceAlias, string title, string intro) {
			var logs = ReadLogs();
			// State detection uses the unfiltered senderLogs; the filtered featureLogs only feed the visible panels (the filter would strip the Disabled marker).
			var senderLogs = logs.Where(log => log.Sender.Equals(config.Sender, StringComparison.OrdinalIgnoreCase)).ToList();
			var featureLogs = senderLogs.Where(log => !IsAdminLifecycleLine(log.Message)).ToList();

			var last = senderLogs.LastOrDefault();
			var nextCandidate = featureLogs.LastOrDefault(log => config.NextCheckMarkers.Any(marker => log.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)));
			DateTime? nextExecution = nextCandidate == null ? null : ParseNextExecution(nextCandidate.Message, config.NextCheckMarkers);

			var state = "Unknown";
			if (last != null) {
				if (last.Message.Contains("not enabled by settings", StringComparison.OrdinalIgnoreCase))
					state = "Disabled";
				else if (IsError(last))
					state = "Error";
				else if (DateTime.UtcNow - last.DateTimeUtc.ToUniversalTime() <= TimeSpan.FromMinutes(10))
					state = "Active";
				else
					state = "Stale";
			}

			var since = DateTime.UtcNow.AddHours(-24);
			var dispatchesToday = string.IsNullOrEmpty(config.DispatchMarker)
				? 0
				: featureLogs.Count(log => log.DateTimeUtc.ToUniversalTime() >= since &&
					log.Message.Contains(config.DispatchMarker, StringComparison.OrdinalIgnoreCase));

			var (liveFleets, liveFleetsAvailable) = await ReadLiveFleets(config.LiveFleetsMission, instanceAlias);
			var results = ReadResults(config.ResultsFileName);
			var maxSlots = config.LiveMaxSlotsFromExpeditionPool
				? await ReadLiveExpeditionSlotTotal(instanceAlias)
				: ReadMaxSlots(config.MaxSlotsSettingSection, config.MaxSlotsSettingKey, instanceAlias);

			return new FeatureDashboardModel {
				FeatureTitle = title,
				FeatureIntro = intro,
				SelectedInstance = instanceAlias,
				Worker = new FeatureWorkerStatus {
					State = state,
					LastActivityUtc = last?.DateTimeUtc.ToUniversalTime(),
					NextExecutionUtc = nextExecution?.ToUniversalTime(),
					NextExecutionText = nextExecution.HasValue ? nextExecution.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown"
				},
				DispatchesToday = dispatchesToday,
				MaxSlots = maxSlots,
				LiveFleets = liveFleets,
				LiveFleetsAvailable = liveFleetsAvailable,
				Results = results,
				ResultsTotal = ReadLifetimeTotal(config.ResultsFileName) ?? new FeatureResultsTotal {
					Metal = results.Sum(r => r.Metal),
					Crystal = results.Sum(r => r.Crystal),
					Deuterium = results.Sum(r => r.Deuterium),
					Darkmatter = results.Sum(r => r.Darkmatter),
					ArtifactsFound = results.Sum(r => r.ArtifactsFound)
				},
				ResultsShowArtifactsColumn = config.ResultsShowArtifactsColumn,
				Logs = ToLogRows(featureLogs.TakeLast(120).Reverse()),
				Errors = ToLogRows(featureLogs.Where(IsError).TakeLast(40).Reverse())
			};
		}

		// Permanent totals from the .db counterpart of ResultsFileName (the JSON store's own sum covers only its last 200 entries); null when the .db is missing so the caller can fall back.
		private static FeatureResultsTotal? ReadLifetimeTotal(string? fileName) {
			if (string.IsNullOrWhiteSpace(fileName))
				return null;
			try {
				var dbFileName = Path.ChangeExtension(Path.GetFileName(fileName), ".db");
				var path = Path.Combine(AppContext.BaseDirectory, "data", dbFileName);
				if (!File.Exists(path))
					return null;

				using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
					DataSource = path,
					Pooling = false
				}.ToString());
				connection.Open();
				using var command = connection.CreateCommand();
				command.CommandText = @"
SELECT COALESCE(SUM(metal),0), COALESCE(SUM(crystal),0), COALESCE(SUM(deuterium),0),
	COALESCE(SUM(darkmatter),0), COALESCE(SUM(artifacts_found),0)
FROM results;";
				using var reader = command.ExecuteReader();
				if (!reader.Read())
					return null;
				return new FeatureResultsTotal {
					Metal = reader.GetInt64(0),
					Crystal = reader.GetInt64(1),
					Deuterium = reader.GetInt64(2),
					Darkmatter = reader.GetInt64(3),
					ArtifactsFound = reader.GetInt64(4)
				};
			} catch {
				return null;
			}
		}

		private static List<FeatureResultRow> ReadResults(string? fileName) {
			if (string.IsNullOrWhiteSpace(fileName))
				return new List<FeatureResultRow>();

			try {
				var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
				if (!File.Exists(path))
					return new List<FeatureResultRow>();

				var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<FeatureResultRow>>(File.ReadAllText(path));
				return (entries ?? new List<FeatureResultRow>())
					.OrderByDescending(e => e.TimestampUtc)
					.ToList();
			} catch {
				return new List<FeatureResultRow>();
			}
		}

		private static int? ReadMaxSlots(string? settingSection, string settingKey, string instanceAlias) {
			if (string.IsNullOrWhiteSpace(settingSection))
				return null;

			var instance = ReadInstanceSettingsJObject(instanceAlias);
			return (instance?[settingSection] as JObject)?.Value<int?>(settingKey);
		}

		private static JObject? ReadInstanceSettingsJObject(string instanceAlias) {
			try {
				var globalPath = SettingsService.GlobalSettingsPath;
				if (string.IsNullOrWhiteSpace(globalPath) || !File.Exists(globalPath))
					globalPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
				var global = JObject.Parse(File.ReadAllText(globalPath));
				var instancePath = ResolveInstanceSettingsPath(global, instanceAlias, Path.GetDirectoryName(globalPath));
				if (!File.Exists(instancePath))
					return null;
				return JObject.Parse(File.ReadAllText(instancePath));
			} catch {
				return null;
			}
		}

		// Live fleets come straight from ogamed's /bot/fleets, so they reflect the account whether or not TBot sent them.
		private static async Task<int?> ReadLiveExpeditionSlotTotal(string instanceAlias) {
			var instance = ReadInstanceSettingsJObject(instanceAlias);
			var general = instance?["General"] as JObject;
			var host = general?.Value<string>("Host");
			var port = general?.Value<string>("Port");
			if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port))
				return null;

			try {
				var response = await Http.GetAsync($"http://{host}:{port}/bot/fleets/slots");
				if (!response.IsSuccessStatusCode)
					return null;

				var envelope = JObject.Parse(await response.Content.ReadAsStringAsync());
				return ((envelope["Result"] as JObject) ?? (envelope["result"] as JObject))?.Value<int?>("ExpTotal");
			} catch {
				return null;
			}
		}

		private static async Task<(IReadOnlyList<LiveFleetRow> Fleets, bool Available)> ReadLiveFleets(Missions? missionFilter, string instanceAlias) {
			if (missionFilter == null)
				return (Array.Empty<LiveFleetRow>(), false);

			var instance = ReadInstanceSettingsJObject(instanceAlias);
			var general = instance?["General"] as JObject;
			var host = general?.Value<string>("Host");
			var port = general?.Value<string>("Port");
			if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port))
				return (Array.Empty<LiveFleetRow>(), false);

			try {
				var response = await Http.GetAsync($"http://{host}:{port}/bot/fleets");
				if (!response.IsSuccessStatusCode)
					return (Array.Empty<LiveFleetRow>(), false);

				var envelope = JObject.Parse(await response.Content.ReadAsStringAsync());
				var fleets = (envelope["Result"] as JArray) ?? (envelope["result"] as JArray) ?? new JArray();
				var missionValue = (int) missionFilter.Value;
				var rows = fleets
					.OfType<JObject>()
					.Where(f => f.Value<int?>("Mission") == missionValue)
					.Select(f => new LiveFleetRow {
						Mission = missionFilter.Value.ToString(),
						Origin = FormatCoordinate(f["Origin"] as JObject),
						Destination = FormatCoordinate(f["Destination"] as JObject),
						ReturnFlight = f.Value<bool?>("ReturnFlight") ?? false,
						ArrivalTimeUtc = f.Value<DateTime?>("ArrivalTime"),
						TotalResources = SumResources(f["Resources"] as JObject)
					})
					.ToList();
				return (rows, true);
			} catch {
				return (Array.Empty<LiveFleetRow>(), false);
			}
		}

		private static string FormatCoordinate(JObject? coordinate) {
			if (coordinate == null)
				return "Unknown";
			return $"[{coordinate.Value<int?>("Galaxy") ?? 0}:{coordinate.Value<int?>("System") ?? 0}:{coordinate.Value<int?>("Position") ?? 0}]";
		}

		private static long SumResources(JObject? resources) {
			if (resources == null)
				return 0;
			return (resources.Value<long?>("Metal") ?? 0) + (resources.Value<long?>("Crystal") ?? 0) + (resources.Value<long?>("Deuterium") ?? 0);
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

		private static DateTime? ParseNextExecution(string message, string[] markers) {
			foreach (var marker in markers) {
				var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
				if (start < 0)
					continue;
				start += marker.Length;
				var end = message.IndexOf(" (", start, StringComparison.Ordinal);
				var value = end > start ? message[start..end] : message[start..];
				if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
					return parsed;
			}
			return null;
		}

		private static bool IsError(LogRecord log) => log.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) || log.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || log.Message.Contains("Exception", StringComparison.OrdinalIgnoreCase);

		// Worker lifecycle lines are plumbing already summarized by the Worker status panel, not feature-specific activity.
		private static bool IsAdminLifecycleLine(string message) {
			// Contains, not StartsWith: the stored message carries a "[Player@Server] " account prefix and sometimes a trailing ".".
			return message.Contains("Starting Worker \"", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("Restarting Worker \"", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("Closing Worker \"", StringComparison.OrdinalIgnoreCase)
				|| (message.Contains("Worker \"", StringComparison.OrdinalIgnoreCase) && message.Contains("closed!", StringComparison.OrdinalIgnoreCase))
				|| message.Contains("not enabled by settings", StringComparison.OrdinalIgnoreCase);
		}

		private static List<FeatureLogRow> ToLogRows(IEnumerable<LogRecord> logs) {
			return logs.Select(log => new FeatureLogRow {
				Level = log.Level,
				Sender = log.Sender,
				DateTimeUtc = log.DateTimeUtc.ToUniversalTime(),
				Message = log.Message,
				CopyText = $"[{log.DateTimeUtc:yyyy-MM-dd HH:mm:ss zzz}] [{log.Sender}] [{log.Level}] {log.Message}"
			}).ToList();
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

		private sealed class LogEntry {
			public string type { get; set; } = string.Empty;
			public string sender { get; set; } = string.Empty;
			public string datetime { get; set; } = string.Empty;
			public string message { get; set; } = string.Empty;
		}

		private sealed record LogRecord(string Level, string Sender, DateTime DateTimeUtc, string Message);
	}
}
