using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	/// <summary>SQLite-backed history of per-celestial resource production, one row appended per celestial per AutoMine cycle, so the WebUI has a real time series.</summary>
	public class ResourceStatsStore : IDisposable {
		private readonly SqliteConnection _db;

		private ResourceStatsStore(SqliteConnection db) {
			_db = db;
		}

		// Shares the per-instance data_{alias}.db file with the other caches, using its own prefixed table so the schemas don't collide.
		public static string GetDatabaseFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			var dataDir = Path.Combine(directory ?? ".", "data");
			Directory.CreateDirectory(dataDir);
			return Path.Combine(dataDir, $"data_{instanceAlias}.db");
		}

		public static async Task<ResourceStatsStore> Load(string instanceSettingsPath, string instanceAlias) {
			var dbPath = GetDatabaseFilePath(instanceSettingsPath, instanceAlias);
			var db = new SqliteConnection($"Data Source={dbPath}");
			await db.OpenAsync();

			using (var cmd = db.CreateCommand()) {
				cmd.CommandText = @"
					CREATE TABLE IF NOT EXISTS resource_stats_snapshot (
						id INTEGER PRIMARY KEY AUTOINCREMENT,
						celestial_id INTEGER NOT NULL,
						celestial_name TEXT,
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						position INTEGER NOT NULL,
						celestial_type INTEGER NOT NULL,
						metal INTEGER NOT NULL,
						crystal INTEGER NOT NULL,
						deuterium INTEGER NOT NULL,
						metal_hourly_production INTEGER NOT NULL,
						crystal_hourly_production INTEGER NOT NULL,
						deuterium_hourly_production INTEGER NOT NULL,
						energy INTEGER NOT NULL,
						recorded_at_utc TEXT NOT NULL
					);
					CREATE INDEX IF NOT EXISTS idx_resource_stats_celestial_time
						ON resource_stats_snapshot (celestial_id, recorded_at_utc);
				";
				await cmd.ExecuteNonQueryAsync();
			}

			return new ResourceStatsStore(db);
		}

		public async Task RecordSnapshot(Celestial celestial, Resources hourlyProduction) {
			try {
				using var cmd = _db.CreateCommand();
				cmd.CommandText = @"
					INSERT INTO resource_stats_snapshot
						(celestial_id, celestial_name, galaxy, system, position, celestial_type,
						 metal, crystal, deuterium,
						 metal_hourly_production, crystal_hourly_production, deuterium_hourly_production,
						 energy, recorded_at_utc)
					VALUES
						($celestialId, $celestialName, $galaxy, $system, $position, $celestialType,
						 $metal, $crystal, $deuterium,
						 $metalProd, $crystalProd, $deutProd,
						 $energy, $recordedAt);
				";
				cmd.Parameters.AddWithValue("$celestialId", celestial.ID);
				cmd.Parameters.AddWithValue("$celestialName", celestial.Name ?? "");
				cmd.Parameters.AddWithValue("$galaxy", (int) celestial.Coordinate.Galaxy);
				cmd.Parameters.AddWithValue("$system", (int) celestial.Coordinate.System);
				cmd.Parameters.AddWithValue("$position", (int) celestial.Coordinate.Position);
				cmd.Parameters.AddWithValue("$celestialType", (int) celestial.Coordinate.Type);
				cmd.Parameters.AddWithValue("$metal", celestial.Resources?.Metal ?? 0);
				cmd.Parameters.AddWithValue("$crystal", celestial.Resources?.Crystal ?? 0);
				cmd.Parameters.AddWithValue("$deuterium", celestial.Resources?.Deuterium ?? 0);
				cmd.Parameters.AddWithValue("$metalProd", hourlyProduction?.Metal ?? 0);
				cmd.Parameters.AddWithValue("$crystalProd", hourlyProduction?.Crystal ?? 0);
				cmd.Parameters.AddWithValue("$deutProd", hourlyProduction?.Deuterium ?? 0);
				cmd.Parameters.AddWithValue("$energy", celestial.Resources?.Energy ?? 0);
				cmd.Parameters.AddWithValue("$recordedAt", DateTime.UtcNow.ToString("O"));
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort persistence - a transient write failure here must never abort AutoMine.
			}
		}

		/// <summary>Removes the rows of celestials that no longer exist, since snapshots are only appended and an abandoned planet would otherwise linger with frozen numbers.</summary>
		public async Task PruneRemovedCelestials(IEnumerable<int> currentCelestialIds) {
			try {
				var ids = currentCelestialIds?.Distinct().ToList() ?? new List<int>();
				if (ids.Count == 0) return; // never wipe everything on an empty/failed celestial list
				using var cmd = _db.CreateCommand();
				var placeholders = string.Join(",", ids.Select((_, i) => $"$id{i}"));
				cmd.CommandText = $"DELETE FROM resource_stats_snapshot WHERE celestial_id NOT IN ({placeholders});";
				for (int i = 0; i < ids.Count; i++)
					cmd.Parameters.AddWithValue($"$id{i}", ids[i]);
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort - a failed prune must never abort the calling worker's cycle.
			}
		}

		/// <summary>Deletes snapshots older than the given retention window, keeping the table bounded.</summary>
		public async Task Prune(TimeSpan retention) {
			try {
				var cutoff = DateTime.UtcNow.Subtract(retention);
				using var cmd = _db.CreateCommand();
				cmd.CommandText = "DELETE FROM resource_stats_snapshot WHERE recorded_at_utc < $cutoff;";
				cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort - a failed prune must never abort the calling worker's cycle.
			}
		}

		public void Dispose() {
			_db?.Dispose();
		}
	}
}
