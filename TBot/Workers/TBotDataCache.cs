using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	/// <summary>SQLite-backed cache for AutoHarvest's galaxy scan results and debris field history, so it survives a restart instead of resetting.</summary>
	public class TBotDataCache : IDisposable {
		private readonly SqliteConnection _db;

		private TBotDataCache(SqliteConnection db) {
			_db = db;
		}

		private static string GetPositionKey(int galaxy, int system, int position) {
			return $"{galaxy}:{system}:{position}";
		}

		// Shares the per-instance data_{alias}.db file with the other caches, each with its own prefixed tables.
		public static string GetDatabaseFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			var dataDir = Path.Combine(directory ?? ".", "data");
			Directory.CreateDirectory(dataDir);
			return Path.Combine(dataDir, $"data_{instanceAlias}.db");
		}

		public static async Task<TBotDataCache> Load(string instanceSettingsPath, string instanceAlias) {
			var dbPath = GetDatabaseFilePath(instanceSettingsPath, instanceAlias);
			var db = new SqliteConnection($"Data Source={dbPath}");
			await db.OpenAsync();

			using (var cmd = db.CreateCommand()) {
				cmd.CommandText = @"
					CREATE TABLE IF NOT EXISTS harvest_system_scan (
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						scanned_at TEXT NOT NULL,
						PRIMARY KEY (galaxy, system)
					);

					CREATE TABLE IF NOT EXISTS harvest_debris_history (
						position_key TEXT PRIMARY KEY,
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						position INTEGER NOT NULL,
						metal INTEGER NOT NULL,
						crystal INTEGER NOT NULL,
						times_seen INTEGER NOT NULL DEFAULT 1,
						first_seen_date TEXT NOT NULL,
						last_seen_date TEXT NOT NULL
					);
					CREATE INDEX IF NOT EXISTS idx_harvest_debris_history_galaxy_system
						ON harvest_debris_history (galaxy, system);

					-- Permanent, unbounded log of harvest dispatches - resources recorded are the
					-- debris field's reported amount at the moment the recycler fleet was sent, same
					-- convention AutoFarm's attacks table already uses for loot (the pre-mission
					-- report value, not a post-hoc verified collection). Requested live 2026-09-02:
					-- user wants a permanent Metal/Crystal/Deuterium total for AutoHarvest, which had
					-- no results store wired at all before this.
					CREATE TABLE IF NOT EXISTS harvest_results (
						id INTEGER PRIMARY KEY AUTOINCREMENT,
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						position INTEGER NOT NULL,
						metal INTEGER NOT NULL DEFAULT 0,
						crystal INTEGER NOT NULL DEFAULT 0,
						deuterium INTEGER NOT NULL DEFAULT 0,
						dispatched_at_utc TEXT NOT NULL
					);

					CREATE TABLE IF NOT EXISTS autodiscovery_cursor (
						origin_key TEXT PRIMARY KEY,
						system INTEGER NOT NULL,
						next_position INTEGER NOT NULL,
						updated_at TEXT NOT NULL
					);

					CREATE TABLE IF NOT EXISTS harvest_full_scan_state (
						id INTEGER PRIMARY KEY CHECK (id = 1),
						last_full_scan_at TEXT NOT NULL
					);
				";
				await cmd.ExecuteNonQueryAsync();
			}

			return new TBotDataCache(db);
		}

		// The AutoDiscovery cursor survives a restart instead of restarting at system/position 1 on every deploy cycle.



		// --- Galaxy scan cache (lets AutoHarvest.ScanRange skip re-fetching a system it already
		// scanned recently, surviving a bot restart instead of resetting to empty every time) ---

		public void MarkSystemScanned(int galaxy, int system) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO harvest_system_scan (galaxy, system, scanned_at)
				VALUES ($galaxy, $system, $scannedAt)
				ON CONFLICT(galaxy, system) DO UPDATE SET scanned_at = excluded.scanned_at;
			";
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$system", system);
			cmd.Parameters.AddWithValue("$scannedAt", DateTime.UtcNow.ToString("O"));
			cmd.ExecuteNonQuery();
		}

		/// <summary>Time since this system was last scanned, or null if never scanned.</summary>
		public TimeSpan? GetSystemScanAge(int galaxy, int system) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT scanned_at FROM harvest_system_scan WHERE galaxy = $galaxy AND system = $system";
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$system", system);
			var result = cmd.ExecuteScalar();
			if (result == null) return null;
			var scannedAt = DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
			return DateTime.UtcNow - scannedAt;
		}

		// --- Debris history (frequency tracking - which positions keep producing debris) ---

		public void RecordDebrisSighting(int galaxy, int system, int position, long metal, long crystal) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO harvest_debris_history (position_key, galaxy, system, position, metal, crystal, times_seen, first_seen_date, last_seen_date)
				VALUES ($key, $galaxy, $system, $position, $metal, $crystal, 1, $now, $now)
				ON CONFLICT(position_key) DO UPDATE SET
					metal = excluded.metal,
					crystal = excluded.crystal,
					times_seen = harvest_debris_history.times_seen + 1,
					last_seen_date = excluded.last_seen_date;
			";
			cmd.Parameters.AddWithValue("$key", GetPositionKey(galaxy, system, position));
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$system", system);
			cmd.Parameters.AddWithValue("$position", position);
			cmd.Parameters.AddWithValue("$metal", metal);
			cmd.Parameters.AddWithValue("$crystal", crystal);
			cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
			cmd.ExecuteNonQuery();
		}

		public void RecordHarvest(int galaxy, int system, int position, long metal, long crystal, long deuterium = 0) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO harvest_results (galaxy, system, position, metal, crystal, deuterium, dispatched_at_utc)
				VALUES ($galaxy, $system, $position, $metal, $crystal, $deuterium, $now);
			";
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$system", system);
			cmd.Parameters.AddWithValue("$position", position);
			cmd.Parameters.AddWithValue("$metal", metal);
			cmd.Parameters.AddWithValue("$crystal", crystal);
			cmd.Parameters.AddWithValue("$deuterium", deuterium);
			cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
			cmd.ExecuteNonQuery();
		}



		// Full-scan throttling: the expensive ScanRange sweep runs every few hours instead of every cycle, since it alone could exceed the watchdog's stuck window.

		public DateTime? GetLastFullScanTime() {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT last_full_scan_at FROM harvest_full_scan_state WHERE id = 1";
			var result = cmd.ExecuteScalar();
			if (result == null) return null;
			return DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
		}

		public void SetLastFullScanTime(DateTime time) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO harvest_full_scan_state (id, last_full_scan_at)
				VALUES (1, $now)
				ON CONFLICT(id) DO UPDATE SET last_full_scan_at = excluded.last_full_scan_at;
			";
			cmd.Parameters.AddWithValue("$now", time.ToUniversalTime().ToString("O"));
			cmd.ExecuteNonQuery();
		}

		/// <summary>Debris positions seen recently enough to be worth checking, above the resource threshold.</summary>
		/// <remarks>The caller must still live-check each candidate it sends a fleet to, since debris fields deplete.</remarks>
		public List<(int Galaxy, int System, int Position, long Metal, long Crystal)> GetCachedDebrisCandidates(long minResources, TimeSpan maxAge) {
			var result = new List<(int, int, int, long, long)>();
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				SELECT galaxy, system, position, metal, crystal FROM harvest_debris_history
				WHERE (metal + crystal) >= $minResources AND last_seen_date >= $cutoff
				ORDER BY (metal + crystal) DESC;
			";
			cmd.Parameters.AddWithValue("$minResources", minResources);
			cmd.Parameters.AddWithValue("$cutoff", (DateTime.UtcNow - maxAge).ToString("O"));
			using var reader = cmd.ExecuteReader();
			while (reader.Read()) {
				result.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)));
			}
			return result;
		}

		public void Dispose() {
			_db?.Dispose();
		}
	}
}
