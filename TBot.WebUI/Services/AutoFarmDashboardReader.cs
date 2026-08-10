using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;

namespace TBot.WebUI.Services {
	public sealed class AutoFarmDashboardReader {
		public AutoFarmDashboardStorage Read(string instanceAlias) {
			var storage = new AutoFarmDashboardStorage();
			var databasePath = GetDatabasePath(instanceAlias);
			if (!File.Exists(databasePath))
				return storage;

			try {
				using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
					DataSource = databasePath,
					Pooling = false
				}.ToString());
				connection.Open();

				using (var systems = connection.CreateCommand()) {
					systems.CommandText = "SELECT COUNT(*), MAX(observed_at_utc) FROM systems;";
					using var reader = systems.ExecuteReader();
					if (reader.Read()) {
						storage.CachedSystemCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
						storage.LastSystemObservedAtUtc = reader.IsDBNull(1) ? null : ParseDate(reader.GetString(1));
					}
				}

				using (var targets = connection.CreateCommand()) {
					var hasConsumedReportColumn = HasColumn(connection, "targets", "consumed_report_id");
					targets.CommandText = @"
SELECT name, state, galaxy, system, position, celestial_type, report_json,
  " + (hasConsumedReportColumn ? "consumed_report_id" : "NULL") + @", updated_at_utc
FROM targets
WHERE report_json IS NOT NULL
ORDER BY updated_at_utc DESC;";
					using var reader = targets.ExecuteReader();
					while (reader.Read()) {
						var report = TryParseReport(reader.GetString(6));
						if (report == null)
							continue;

						storage.Reports.Add(new AutoFarmReportRow {
							TargetName = reader.GetString(0),
							State = GetStateName(reader.GetInt32(1)),
							Coordinate = FormatCoordinate(reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)),
							ReportId = report.Value<int?>("ID") ?? 0,
							ReportDateUtc = ParseDate(report.Value<string>("Date")),
							UpdatedAtUtc = ParseDate(reader.GetString(8)),
							Metal = report.Value<long?>("Metal") ?? 0,
							Crystal = report.Value<long?>("Crystal") ?? 0,
							Deuterium = report.Value<long?>("Deuterium") ?? 0,
							ConsumedReportId = reader.IsDBNull(7) ? null : reader.GetInt32(7)
						});
					}
				}

				if (HasTable(connection, "attacks")) {
					using (var attacks = connection.CreateCommand()) {
						attacks.CommandText = @"
SELECT report_id, galaxy, system, position, celestial_type, target_name,
  dispatched_at_utc, metal, crystal, deuterium
FROM attacks
ORDER BY dispatched_at_utc DESC
LIMIT 100;";
						using var reader = attacks.ExecuteReader();
						while (reader.Read()) {
							storage.Attacks.Add(new AutoFarmAttackRow {
								ReportId = reader.GetInt32(0),
								Coordinate = FormatCoordinate(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)),
								TargetName = reader.GetString(5),
								DispatchedAtUtc = ParseDate(reader.GetString(6)) ?? DateTime.MinValue,
								Metal = reader.GetInt64(7),
								Crystal = reader.GetInt64(8),
								Deuterium = reader.GetInt64(9)
							});
						}
					}
				}

				storage.DatabaseAvailable = true;
			} catch {
				// The dashboard is read-only and must remain usable if a database is mid-migration.
				storage.DatabaseAvailable = false;
			}

			return storage;
		}

		private static string GetDatabasePath(string instanceAlias) {
			var fileName = Path.GetFileName($"autofarm_{instanceAlias}.db");
			return Path.Combine(AppContext.BaseDirectory, "data", fileName);
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
			while (reader.Read()) {
				if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		private static JObject TryParseReport(string json) {
			try {
				return JObject.Parse(json);
			} catch {
				return null;
			}
		}

		private static DateTime? ParseDate(string value) {
			if (string.IsNullOrWhiteSpace(value) || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
				return null;
			return parsed.ToUniversalTime();
		}

		private static string GetStateName(int state) {
			return Enum.IsDefined(typeof(FarmState), state)
				? ((FarmState) state).ToString()
				: "Unknown";
		}

		private static string FormatCoordinate(int galaxy, int system, int position, int celestialType) {
			var code = (Celestials) celestialType switch {
				Celestials.Moon => "M",
				Celestials.Planet => "P",
				_ => "C"
			};
			return $"[{code}:{galaxy}:{system}:{position}]";
		}
	}

	public sealed class AutoFarmDashboardStorage {
		public bool DatabaseAvailable { get; set; }
		public int CachedSystemCount { get; set; }
		public DateTime? LastSystemObservedAtUtc { get; set; }
		public List<AutoFarmReportRow> Reports { get; } = new();
		public List<AutoFarmAttackRow> Attacks { get; } = new();
	}
}
