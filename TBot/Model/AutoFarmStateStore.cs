using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public sealed class AutoFarmSystemSnapshot {
		public int Galaxy { get; init; }
		public int System { get; init; }
		public DateTime ObservedAtUtc { get; init; }
		public bool IsEmpty { get; init; }
		public List<Planet> Planets { get; init; } = new();
	}

	public sealed class AutoFarmAttackRecord {
		public int ReportId { get; init; }
		public string TargetName { get; init; }
		public Coordinate Coordinate { get; init; }
		public DateTime DispatchedAtUtc { get; init; }
		public Resources Loot { get; init; } = new();
	}

	/// <summary>
	/// Durable AutoFarm state. System snapshots and target decisions survive a
	/// worker restart without serializing the whole TBot instance.
	/// </summary>
	public sealed class AutoFarmStateStore : IDisposable {
		private readonly string _connectionString;
		private bool _enabled = true;

		static AutoFarmStateStore() {
			SQLitePCL.Batteries_V2.Init();
		}

		public AutoFarmStateStore(string fileName, string dataFolder = null) {
			var folder = dataFolder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			Directory.CreateDirectory(folder);

			var safeFileName = Path.GetFileName(fileName);
			if (string.IsNullOrWhiteSpace(safeFileName))
				safeFileName = "autofarm.db";
			if (!safeFileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
				safeFileName += ".db";

			var path = Path.Combine(folder, safeFileName);
			_connectionString = new SqliteConnectionStringBuilder {
				DataSource = path,
				Pooling = false
			}.ToString();
			Initialize();
		}

		public AutoFarmSystemSnapshot GetFreshSystemSnapshot(int galaxy, int system, DateTime nowUtc, TimeSpan ttl) {
			if (!_enabled || ttl <= TimeSpan.Zero)
				return null;

			try {
				var snapshot = GetSystemSnapshot(galaxy, system);
				if (snapshot == null || nowUtc - snapshot.ObservedAtUtc >= ttl)
					return null;

				return snapshot;
			} catch {
				return null;
			}
		}

		public AutoFarmSystemSnapshot GetSystemSnapshot(int galaxy, int system) {
			if (!_enabled)
				return null;

			try {
				using var connection = OpenConnection();
				using var command = connection.CreateCommand();
				command.CommandText = @"
SELECT observed_at_utc, is_empty, planets_json
FROM systems
WHERE galaxy = $galaxy AND system = $system;";
				command.Parameters.AddWithValue("$galaxy", galaxy);
				command.Parameters.AddWithValue("$system", system);

				using var reader = command.ExecuteReader();
				if (!reader.Read())
					return null;

				return new AutoFarmSystemSnapshot {
					Galaxy = galaxy,
					System = system,
					ObservedAtUtc = ParseUtc(reader.GetString(0)),
					IsEmpty = reader.GetInt64(1) != 0,
					Planets = JsonConvert.DeserializeObject<List<Planet>>(reader.GetString(2)) ?? new List<Planet>()
				};
			} catch {
				return null;
			}
		}

		public void SaveSystemSnapshot(int galaxy, int system, DateTime observedAtUtc, bool isEmpty, IEnumerable<Planet> planets) {
			if (!_enabled)
				return;

			try {
				using var connection = OpenConnection();
				using var command = connection.CreateCommand();
				command.CommandText = @"
INSERT INTO systems (galaxy, system, observed_at_utc, is_empty, planets_json)
VALUES ($galaxy, $system, $observed_at_utc, $is_empty, $planets_json)
ON CONFLICT(galaxy, system) DO UPDATE SET
  observed_at_utc = excluded.observed_at_utc,
  is_empty = excluded.is_empty,
  planets_json = excluded.planets_json;";
				command.Parameters.AddWithValue("$galaxy", galaxy);
				command.Parameters.AddWithValue("$system", system);
				command.Parameters.AddWithValue("$observed_at_utc", FormatUtc(observedAtUtc));
				command.Parameters.AddWithValue("$is_empty", isEmpty ? 1 : 0);
				command.Parameters.AddWithValue("$planets_json",
					JsonConvert.SerializeObject(planets ?? Array.Empty<Planet>()));
				command.ExecuteNonQuery();
			} catch {
				// Persistence must never stop AutoFarm.
			}
		}

		public void UpsertTarget(FarmTarget target, DateTime updatedAtUtc) {
			if (!_enabled || target?.Celestial?.Coordinate == null)
				return;

			try {
				var coordinate = target.Celestial.Coordinate;
				using var connection = OpenConnection();
				using var command = connection.CreateCommand();
				command.CommandText = @"
INSERT INTO targets (
  galaxy, system, position, celestial_type, name, state, report_json, consumed_report_id, updated_at_utc)
VALUES ($galaxy, $system, $position, $celestial_type, $name, $state, $report_json, $consumed_report_id, $updated_at_utc)
ON CONFLICT(galaxy, system, position, celestial_type) DO UPDATE SET
  name = excluded.name,
  state = excluded.state,
  report_json = excluded.report_json,
  consumed_report_id = excluded.consumed_report_id,
  updated_at_utc = excluded.updated_at_utc;";
				command.Parameters.AddWithValue("$galaxy", coordinate.Galaxy);
				command.Parameters.AddWithValue("$system", coordinate.System);
				command.Parameters.AddWithValue("$position", coordinate.Position);
				command.Parameters.AddWithValue("$celestial_type", (int) coordinate.Type);
				command.Parameters.AddWithValue("$name", target.Celestial.Name ?? string.Empty);
				command.Parameters.AddWithValue("$state", (int) target.State);
				command.Parameters.AddWithValue("$report_json", target.Report == null
					? DBNull.Value
					: JsonConvert.SerializeObject(target.Report));
				command.Parameters.AddWithValue("$consumed_report_id", target.ConsumedReportId.HasValue
					? target.ConsumedReportId.Value
					: DBNull.Value);
				command.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc));
				command.ExecuteNonQuery();
			} catch {
				// Persistence must never stop AutoFarm.
			}
		}

		public List<FarmTarget> LoadTargets() {
			var targets = new List<FarmTarget>();
			if (!_enabled)
				return targets;

			try {
				using var connection = OpenConnection();
				using var command = connection.CreateCommand();
				command.CommandText = @"
SELECT galaxy, system, position, celestial_type, name, state, report_json, consumed_report_id
FROM targets;";

				using var reader = command.ExecuteReader();
				while (reader.Read()) {
					var coordinate = new Coordinate(
						reader.GetInt32(0),
						reader.GetInt32(1),
						reader.GetInt32(2),
						(Celestials) reader.GetInt32(3));
					var celestial = new Planet {
						Name = reader.GetString(4),
						Coordinate = coordinate
					};
					var report = reader.IsDBNull(6)
						? null
						: JsonConvert.DeserializeObject<EspionageReport>(reader.GetString(6));
					targets.Add(new FarmTarget(celestial, (FarmState) reader.GetInt32(5), report) {
						ConsumedReportId = reader.IsDBNull(7) ? null : reader.GetInt32(7)
					});
				}
			} catch {
				return new List<FarmTarget>();
			}

			return targets;
		}

		public bool HasConsumedReport(int reportId) {
			if (!_enabled || reportId <= 0)
				return false;

			try {
				using var connection = OpenConnection();
				using var command = connection.CreateCommand();
				command.CommandText = "SELECT EXISTS(SELECT 1 FROM consumed_reports WHERE report_id = $report_id);";
				command.Parameters.AddWithValue("$report_id", reportId);
				return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
			} catch {
				return false;
			}
		}

		public void RecordAttack(FarmTarget target, Resources loot, DateTime dispatchedAtUtc) {
			if (!_enabled || target?.Celestial?.Coordinate == null || target.Report?.ID <= 0)
				return;

			try {
				var coordinate = target.Celestial.Coordinate;
				using var connection = OpenConnection();
				using var transaction = connection.BeginTransaction();

				using (var consumed = connection.CreateCommand()) {
					consumed.Transaction = transaction;
					consumed.CommandText = @"
INSERT OR IGNORE INTO consumed_reports (
  report_id, galaxy, system, position, celestial_type, consumed_at_utc)
VALUES ($report_id, $galaxy, $system, $position, $celestial_type, $consumed_at_utc);";
					consumed.Parameters.AddWithValue("$report_id", target.Report.ID);
					consumed.Parameters.AddWithValue("$galaxy", coordinate.Galaxy);
					consumed.Parameters.AddWithValue("$system", coordinate.System);
					consumed.Parameters.AddWithValue("$position", coordinate.Position);
					consumed.Parameters.AddWithValue("$celestial_type", (int) coordinate.Type);
					consumed.Parameters.AddWithValue("$consumed_at_utc", FormatUtc(dispatchedAtUtc));
					consumed.ExecuteNonQuery();
				}

				using (var attack = connection.CreateCommand()) {
					attack.Transaction = transaction;
					attack.CommandText = @"
INSERT OR IGNORE INTO attacks (
  report_id, galaxy, system, position, celestial_type, target_name,
  dispatched_at_utc, metal, crystal, deuterium)
VALUES ($report_id, $galaxy, $system, $position, $celestial_type, $target_name,
  $dispatched_at_utc, $metal, $crystal, $deuterium);";
					attack.Parameters.AddWithValue("$report_id", target.Report.ID);
					attack.Parameters.AddWithValue("$galaxy", coordinate.Galaxy);
					attack.Parameters.AddWithValue("$system", coordinate.System);
					attack.Parameters.AddWithValue("$position", coordinate.Position);
					attack.Parameters.AddWithValue("$celestial_type", (int) coordinate.Type);
					attack.Parameters.AddWithValue("$target_name", target.Celestial.Name ?? string.Empty);
					attack.Parameters.AddWithValue("$dispatched_at_utc", FormatUtc(dispatchedAtUtc));
					attack.Parameters.AddWithValue("$metal", loot?.Metal ?? 0);
					attack.Parameters.AddWithValue("$crystal", loot?.Crystal ?? 0);
					attack.Parameters.AddWithValue("$deuterium", loot?.Deuterium ?? 0);
					attack.ExecuteNonQuery();
				}

				transaction.Commit();
			} catch {
				// Persistence must never stop AutoFarm.
			}
		}

		public List<AutoFarmAttackRecord> LoadAttackHistory(int limit = 100) {
			var attacks = new List<AutoFarmAttackRecord>();
			if (!_enabled)
				return attacks;

			try {
				using var connection = OpenConnection();
				using var command = connection.CreateCommand();
				command.CommandText = @"
SELECT report_id, galaxy, system, position, celestial_type, target_name,
  dispatched_at_utc, metal, crystal, deuterium
FROM attacks
ORDER BY dispatched_at_utc DESC
LIMIT $limit;";
				command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

				using var reader = command.ExecuteReader();
				while (reader.Read()) {
					attacks.Add(new AutoFarmAttackRecord {
						ReportId = reader.GetInt32(0),
						Coordinate = new Coordinate(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), (Celestials) reader.GetInt32(4)),
						TargetName = reader.GetString(5),
						DispatchedAtUtc = ParseUtc(reader.GetString(6)),
						Loot = new Resources(reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9))
					});
				}
			} catch {
				return new List<AutoFarmAttackRecord>();
			}

			return attacks;
		}

		public void ReplaceTargets(IEnumerable<FarmTarget> targets, DateTime updatedAtUtc) {
			if (!_enabled)
				return;

			try {
				using var connection = OpenConnection();
				using var transaction = connection.BeginTransaction();
				using (var delete = connection.CreateCommand()) {
					delete.Transaction = transaction;
					delete.CommandText = "DELETE FROM targets;";
					delete.ExecuteNonQuery();
				}

				var uniqueTargets = (targets ?? Array.Empty<FarmTarget>())
					.Where(target => target?.Celestial?.Coordinate != null)
					.GroupBy(target => target.Celestial.Coordinate.ToString())
					.Select(group => group.Last());

				foreach (var target in uniqueTargets) {

					using var insert = connection.CreateCommand();
					insert.Transaction = transaction;
					var coordinate = target.Celestial.Coordinate;
					insert.CommandText = @"
INSERT INTO targets (
  galaxy, system, position, celestial_type, name, state, report_json, consumed_report_id, updated_at_utc)
VALUES ($galaxy, $system, $position, $celestial_type, $name, $state, $report_json, $consumed_report_id, $updated_at_utc);";
					insert.Parameters.AddWithValue("$galaxy", coordinate.Galaxy);
					insert.Parameters.AddWithValue("$system", coordinate.System);
					insert.Parameters.AddWithValue("$position", coordinate.Position);
					insert.Parameters.AddWithValue("$celestial_type", (int) coordinate.Type);
					insert.Parameters.AddWithValue("$name", target.Celestial.Name ?? string.Empty);
					insert.Parameters.AddWithValue("$state", (int) target.State);
					insert.Parameters.AddWithValue("$report_json", target.Report == null
						? DBNull.Value
						: JsonConvert.SerializeObject(target.Report));
					insert.Parameters.AddWithValue("$consumed_report_id", target.ConsumedReportId.HasValue
						? target.ConsumedReportId.Value
						: DBNull.Value);
					insert.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc));
					insert.ExecuteNonQuery();
				}

				transaction.Commit();
			} catch {
				// Persistence must never stop AutoFarm.
			}
		}

		private void Initialize() {
			try {
				using var connection = OpenConnection();
				using var command = connection.CreateCommand();
				command.CommandText = @"
CREATE TABLE IF NOT EXISTS systems (
  galaxy INTEGER NOT NULL,
  system INTEGER NOT NULL,
  observed_at_utc TEXT NOT NULL,
  is_empty INTEGER NOT NULL,
  planets_json TEXT NOT NULL,
  PRIMARY KEY (galaxy, system)
);
CREATE TABLE IF NOT EXISTS targets (
  galaxy INTEGER NOT NULL,
  system INTEGER NOT NULL,
  position INTEGER NOT NULL,
  celestial_type INTEGER NOT NULL,
  name TEXT NOT NULL,
  state INTEGER NOT NULL,
  report_json TEXT NULL,
  consumed_report_id INTEGER NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY (galaxy, system, position, celestial_type)
);
CREATE TABLE IF NOT EXISTS consumed_reports (
  report_id INTEGER NOT NULL PRIMARY KEY,
  galaxy INTEGER NOT NULL,
  system INTEGER NOT NULL,
  position INTEGER NOT NULL,
  celestial_type INTEGER NOT NULL,
  consumed_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS attacks (
  report_id INTEGER NOT NULL PRIMARY KEY,
  galaxy INTEGER NOT NULL,
  system INTEGER NOT NULL,
  position INTEGER NOT NULL,
  celestial_type INTEGER NOT NULL,
  target_name TEXT NOT NULL,
  dispatched_at_utc TEXT NOT NULL,
  metal INTEGER NOT NULL,
  crystal INTEGER NOT NULL,
  deuterium INTEGER NOT NULL
);";
				command.ExecuteNonQuery();

				try {
					using var migration = connection.CreateCommand();
					migration.CommandText = "ALTER TABLE targets ADD COLUMN consumed_report_id INTEGER NULL;";
					migration.ExecuteNonQuery();
				} catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) {
					// Existing AutoFarm databases already have the new column.
				}

				using var backfill = connection.CreateCommand();
				backfill.CommandText = @"
INSERT OR IGNORE INTO consumed_reports (
  report_id, galaxy, system, position, celestial_type, consumed_at_utc)
SELECT consumed_report_id, galaxy, system, position, celestial_type, updated_at_utc
FROM targets
WHERE consumed_report_id IS NOT NULL AND consumed_report_id > 0;";
				backfill.ExecuteNonQuery();
			} catch {
				_enabled = false;
			}
		}

		private SqliteConnection OpenConnection() {
			var connection = new SqliteConnection(_connectionString);
			connection.Open();
			return connection;
		}

		private static string FormatUtc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

		private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

		public void Dispose() {
		}
	}
}
