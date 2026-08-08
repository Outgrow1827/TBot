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
  galaxy, system, position, celestial_type, name, state, report_json, updated_at_utc)
VALUES ($galaxy, $system, $position, $celestial_type, $name, $state, $report_json, $updated_at_utc)
ON CONFLICT(galaxy, system, position, celestial_type) DO UPDATE SET
  name = excluded.name,
  state = excluded.state,
  report_json = excluded.report_json,
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
SELECT galaxy, system, position, celestial_type, name, state, report_json
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
					targets.Add(new FarmTarget(celestial, (FarmState) reader.GetInt32(5), report));
				}
			} catch {
				return new List<FarmTarget>();
			}

			return targets;
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
  galaxy, system, position, celestial_type, name, state, report_json, updated_at_utc)
VALUES ($galaxy, $system, $position, $celestial_type, $name, $state, $report_json, $updated_at_utc);";
					insert.Parameters.AddWithValue("$galaxy", coordinate.Galaxy);
					insert.Parameters.AddWithValue("$system", coordinate.System);
					insert.Parameters.AddWithValue("$position", coordinate.Position);
					insert.Parameters.AddWithValue("$celestial_type", (int) coordinate.Type);
					insert.Parameters.AddWithValue("$name", target.Celestial.Name ?? string.Empty);
					insert.Parameters.AddWithValue("$state", (int) target.State);
					insert.Parameters.AddWithValue("$report_json", target.Report == null
						? DBNull.Value
						: JsonConvert.SerializeObject(target.Report));
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
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY (galaxy, system, position, celestial_type)
);";
				command.ExecuteNonQuery();
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
