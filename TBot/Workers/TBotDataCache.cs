using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	/// <summary>
	/// Persistent storage for AutoHarvest.ScanRange's galaxy scan cache and debris field history,
	/// backed by SQLite (same pattern as FarmTargetCache) instead of an in-memory dictionary that
	/// resets on every bot restart. Also tracks how many times debris was seen at each position -
	/// some players/positions produce debris frequently (repeated combats, moon mining), so this
	/// builds a history instead of throwing it away after each scan.
	/// </summary>
	public class TBotDataCache : IDisposable {
		private readonly SqliteConnection _db;

		private TBotDataCache(SqliteConnection db) {
			_db = db;
		}

		private static string GetPositionKey(int galaxy, int system, int position) {
			return $"{galaxy}:{system}:{position}";
		}

		// Shared with FarmTargetCache (AutoFarm/FastFarm) and AutoDiscovery's cursor persistence -
		// one physical file per instance instead of one per feature, each keeping its own
		// prefixed tables (harvest_*, farm_*, autodiscovery_*) so there's no schema collision.
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

		// --- AutoDiscovery cursor (which system/position each origin last got to) - survives a
		// bot restart instead of resetting to system/position 1 every time, which previously
		// meant re-discovering already-attempted positions after every deploy/restart cycle. ---

		public void SaveDiscoveryCursor(Celestial origin, int system, int nextPosition) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO autodiscovery_cursor (origin_key, system, next_position, updated_at)
				VALUES ($key, $system, $nextPosition, $now)
				ON CONFLICT(origin_key) DO UPDATE SET
					system = excluded.system,
					next_position = excluded.next_position,
					updated_at = excluded.updated_at;
			";
			cmd.Parameters.AddWithValue("$key", $"{origin.Coordinate.Galaxy}:{origin.Coordinate.System}:{origin.Coordinate.Position}:{origin.Coordinate.Type}");
			cmd.Parameters.AddWithValue("$system", system);
			cmd.Parameters.AddWithValue("$nextPosition", nextPosition);
			cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
			cmd.ExecuteNonQuery();
		}

		public (int System, int NextPosition)? LoadDiscoveryCursor(Celestial origin) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT system, next_position FROM autodiscovery_cursor WHERE origin_key = $key";
			cmd.Parameters.AddWithValue("$key", $"{origin.Coordinate.Galaxy}:{origin.Coordinate.System}:{origin.Coordinate.Position}:{origin.Coordinate.Type}");
			using var reader = cmd.ExecuteReader();
			if (reader.Read()) {
				return (reader.GetInt32(0), reader.GetInt32(1));
			}
			return null;
		}

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

		public (long Metal, long Crystal, long Deuterium) GetHarvestLifetimeTotal() {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				SELECT COALESCE(SUM(metal),0), COALESCE(SUM(crystal),0), COALESCE(SUM(deuterium),0)
				FROM harvest_results;
			";
			using var reader = cmd.ExecuteReader();
			if (reader.Read())
				return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
			return (0, 0, 0);
		}

		/// <summary>Positions that have produced debris repeatedly, ordered by how often - useful to
		/// prioritize a wide ScanRange or to eyeball which players are worth watching.</summary>
		public List<(int Galaxy, int System, int Position, int TimesSeen)> GetFrequentPositions(int galaxy, int minTimesSeen = 2) {
			var result = new List<(int, int, int, int)>();
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				SELECT galaxy, system, position, times_seen FROM harvest_debris_history
				WHERE galaxy = $galaxy AND times_seen >= $minTimesSeen
				ORDER BY times_seen DESC;
			";
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$minTimesSeen", minTimesSeen);
			using var reader = cmd.ExecuteReader();
			while (reader.Read()) {
				result.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
			}
			return result;
		}

		// --- Full-scan throttling (AutoHarvest.ScanRange's expensive network sweep across hundreds of
		// systems, gated to run every few hours instead of every Execute() cycle - see project memory
		// 2026-08-19: this scan alone was exceeding Watchdog.MaxStuckMinutes and freezing the whole
		// worker). Between full scans, candidates come straight from harvest_debris_history instead. ---

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

		/// <summary>Debris positions seen recently enough to still be trustworthy without a live
		/// re-scan, above the resource threshold. Caller is still expected to live-check each
		/// candidate it actually intends to send a fleet to before committing (debris fields do
		/// deplete/refresh), this just avoids sweeping the whole ScanRange to find candidates.</summary>
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
