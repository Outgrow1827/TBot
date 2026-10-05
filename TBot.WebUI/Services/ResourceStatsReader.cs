using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;

namespace TBot.WebUI.Services {
	public sealed class ResourceStatsReader {
		public ResourceStatsStorage Read(string instanceAlias) {
			var storage = new ResourceStatsStorage();
			var databasePath = GetDatabasePath(instanceAlias);
			if (!File.Exists(databasePath))
				return storage;

			try {
				using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
					DataSource = databasePath,
					Pooling = false
				}.ToString());
				connection.Open();

				if (!HasTable(connection, "resource_stats_snapshot"))
					return storage;

				storage.DatabaseAvailable = true;
				ReadLatestPerCelestial(connection, storage);
				ReadHistory(connection, storage);
			} catch {
				storage.DatabaseAvailable = false;
			}

			return storage;
		}

		// Latest row per celestial_id - the "current" state shown in the totals/celestial table.
		private static void ReadLatestPerCelestial(SqliteConnection connection, ResourceStatsStorage storage) {
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT s.celestial_id, s.celestial_name, s.galaxy, s.system, s.position, s.celestial_type,
  s.metal, s.crystal, s.deuterium,
  s.metal_hourly_production, s.crystal_hourly_production, s.deuterium_hourly_production,
  s.energy, s.recorded_at_utc
FROM resource_stats_snapshot s
JOIN (
  SELECT celestial_id, MAX(recorded_at_utc) AS max_recorded_at
  FROM resource_stats_snapshot
  GROUP BY celestial_id
) latest ON latest.celestial_id = s.celestial_id AND latest.max_recorded_at = s.recorded_at_utc
ORDER BY s.galaxy, s.system, s.position;";

			using var reader = command.ExecuteReader();
			while (reader.Read()) {
				var recordedAt = ParseDate(reader.GetString(13)) ?? DateTime.MinValue;
				storage.Celestials.Add(new ResourceStatsCelestialRow {
					CelestialName = reader.IsDBNull(1) ? "" : reader.GetString(1),
					Coordinate = FormatCoordinate(reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)),
					Metal = reader.GetInt64(6),
					Crystal = reader.GetInt64(7),
					Deuterium = reader.GetInt64(8),
					MetalHourlyProduction = reader.GetInt64(9),
					CrystalHourlyProduction = reader.GetInt64(10),
					DeuteriumHourlyProduction = reader.GetInt64(11),
					Energy = reader.GetInt64(12),
					RecordedAtUtc = recordedAt
				});
			}

			storage.Totals = new ResourceStatsTotals {
				Metal = storage.Celestials.Sum(c => c.Metal),
				Crystal = storage.Celestials.Sum(c => c.Crystal),
				Deuterium = storage.Celestials.Sum(c => c.Deuterium),
				MetalHourlyProduction = storage.Celestials.Sum(c => c.MetalHourlyProduction),
				CrystalHourlyProduction = storage.Celestials.Sum(c => c.CrystalHourlyProduction),
				DeuteriumHourlyProduction = storage.Celestials.Sum(c => c.DeuteriumHourlyProduction),
				LastUpdatedUtc = storage.Celestials.Count == 0 ? null : storage.Celestials.Max(c => c.RecordedAtUtc)
			};
		}

		// Per-cycle totals across all celestials, most recent 200 cycles - feeds the history chart.
		// Grouped by recorded_at_utc (all celestials in one AutoMine pass share the same timestamp
		// closely enough in practice that grouping by exact value still gives one point per cycle;
		// a cycle spanning multiple seconds just yields a couple of extra nearby points, which the
		// chart already tolerates).
		private static void ReadHistory(SqliteConnection connection, ResourceStatsStorage storage) {
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT recorded_at_utc,
  SUM(metal), SUM(crystal), SUM(deuterium),
  SUM(metal_hourly_production), SUM(crystal_hourly_production), SUM(deuterium_hourly_production)
FROM resource_stats_snapshot
GROUP BY recorded_at_utc
ORDER BY recorded_at_utc DESC
LIMIT 200;";

			using var reader = command.ExecuteReader();
			var points = new List<ResourceStatsHistoryPoint>();
			while (reader.Read()) {
				var recordedAt = ParseDate(reader.GetString(0));
				if (recordedAt == null)
					continue;
				points.Add(new ResourceStatsHistoryPoint {
					RecordedAtUtc = recordedAt.Value,
					Metal = reader.GetInt64(1),
					Crystal = reader.GetInt64(2),
					Deuterium = reader.GetInt64(3),
					MetalHourlyProduction = reader.GetInt64(4),
					CrystalHourlyProduction = reader.GetInt64(5),
					DeuteriumHourlyProduction = reader.GetInt64(6)
				});
			}
			points.Reverse();
			storage.History.AddRange(points);
		}

		private static bool HasTable(SqliteConnection connection, string tableName) {
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
			command.Parameters.AddWithValue("$name", tableName);
			return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
		}

		private static DateTime? ParseDate(string? value) {
			return string.IsNullOrWhiteSpace(value) || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
				? null
				: parsed.ToUniversalTime();
		}

		private static string FormatCoordinate(int galaxy, int system, int position, int celestialType) {
			var code = (Celestials) celestialType == Celestials.Moon ? "M" : "P";
			return $"[{code}:{galaxy}:{system}:{position}]";
		}

		// Same shared file as FarmTargetCache/TBotDataCache/ResourceStatsStore on the bot side
		// (data_{alias}.db) - the WebUI and the bot process run from the same deploy directory,
		// so AppContext.BaseDirectory here resolves to the same physical "data" folder.
		private static string GetDatabasePath(string instanceAlias) {
			return Path.Combine(AppContext.BaseDirectory, "data", Path.GetFileName($"data_{instanceAlias}.db"));
		}
	}

	public sealed class ResourceStatsStorage {
		public bool DatabaseAvailable { get; set; }
		public ResourceStatsTotals Totals { get; set; } = new();
		public List<ResourceStatsCelestialRow> Celestials { get; } = new();
		public List<ResourceStatsHistoryPoint> History { get; } = new();
	}
}
