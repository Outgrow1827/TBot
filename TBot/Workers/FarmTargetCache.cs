using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	/// <summary>SQLite-backed store for AutoFarm's FastFarm target cache and coordinate blacklist: each entry is upserted in its own transaction, and the entry model is kept as one JSON blob per row.</summary>
	public class FarmTargetCache : IDisposable {
		private readonly SqliteConnection _db;
		// In-memory read cache in front of SQLite, so the per-target lookups don't hit the database on every call.
		private readonly Dictionary<string, FarmTargetCacheEntry> _entries = new();

		private FarmTargetCache(SqliteConnection db) {
			_db = db;
		}

		public int ReportRetentionDays { get; set; } = 7;

		/// <summary>Minimum resources threshold for filtering farm targets, shared by FastFarm and AutoFarm and persisted so it survives restarts.</summary>
		public long MinimumResources { get; set; } = 1000000;

		private static string GetKey(Coordinate coord) {
			return $"{coord.Galaxy}:{coord.System}:{coord.Position}:{coord.Type}";
		}

		// One physical file per instance, shared with TBotDataCache and AutoDiscovery's cursor storage, each keeping its own prefixed tables.
		public static string GetDatabaseFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			var dataDir = Path.Combine(directory ?? ".", "data");
			Directory.CreateDirectory(dataDir);
			return Path.Combine(dataDir, $"data_{instanceAlias}.db");
		}

		/// <summary>Clears only the FastFarm target cache rows (not the blacklist) via DELETE FROM on a short-lived connection, since AutoFarmWorker may hold the same file open on Windows.</summary>
		public static async Task<bool> ClearTargetCache(string instanceSettingsPath, string instanceAlias) {
			var dbPath = GetDatabaseFilePath(instanceSettingsPath, instanceAlias);
			if (!File.Exists(dbPath)) return false;

			using var db = new SqliteConnection($"Data Source={dbPath}");
			await db.OpenAsync();
			using var cmd = db.CreateCommand();
			cmd.CommandText = "DELETE FROM farm_target_cache WHERE 1=1";
			try {
				await cmd.ExecuteNonQueryAsync();
			} catch (SqliteException) {
				// Table doesn't exist yet (DB file present but never fully initialized) - nothing to clear.
				return false;
			}
			return true;
		}

		// Pre-P20 cache file: imported once into the new database on first load, and then left in place as a rollback safety net.
		private static string GetLegacyCacheFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			return Path.Combine(directory ?? ".", $"fastfarm_cache_{instanceAlias}.json");
		}

		public static async Task<FarmTargetCache> Load(string instanceSettingsPath, string instanceAlias) {
			var dbPath = GetDatabaseFilePath(instanceSettingsPath, instanceAlias);
			bool isNewDatabase = !File.Exists(dbPath);

			var db = new SqliteConnection($"Data Source={dbPath}");
			await db.OpenAsync();

			using (var cmd = db.CreateCommand()) {
				cmd.CommandText = @"
					CREATE TABLE IF NOT EXISTS farm_target_cache (
						coordinate_key TEXT PRIMARY KEY,
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						last_seen_date TEXT,
						last_report_date TEXT,
						entry_json TEXT NOT NULL
					);
					CREATE INDEX IF NOT EXISTS idx_farm_target_cache_galaxy_system
						ON farm_target_cache (galaxy, system);

					CREATE TABLE IF NOT EXISTS farm_blacklist (
						coordinate_key TEXT PRIMARY KEY,
						blacklisted_until TEXT NOT NULL
					);

					CREATE TABLE IF NOT EXISTS player_blacklist (
						player_name TEXT PRIMARY KEY,
						blacklisted_until TEXT NOT NULL,
						player_id INTEGER NULL
					);

					CREATE TABLE IF NOT EXISTS player_activity (
						player_id INTEGER PRIMARY KEY,
						player_name TEXT NOT NULL,
						is_active INTEGER NOT NULL DEFAULT 0,
						last_checked TEXT NOT NULL,
						activity_days INTEGER
					);
					CREATE INDEX IF NOT EXISTS idx_player_activity_name ON player_activity (player_name);

					CREATE TABLE IF NOT EXISTS system_scan_cache (
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						scanned_at TEXT NOT NULL,
						PRIMARY KEY (galaxy, system)
					);

					CREATE TABLE IF NOT EXISTS attack_history (
						id INTEGER PRIMARY KEY AUTOINCREMENT,
						coordinate_key TEXT NOT NULL,
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						position INTEGER NOT NULL,
						player_name TEXT,
						metal INTEGER NOT NULL,
						crystal INTEGER NOT NULL,
						deuterium INTEGER NOT NULL,
						attacked_at TEXT NOT NULL
					);
					CREATE INDEX IF NOT EXISTS idx_attack_history_coordinate
						ON attack_history (coordinate_key);
					CREATE INDEX IF NOT EXISTS idx_attack_history_player
						ON attack_history (player_name);

					CREATE TABLE IF NOT EXISTS farm_settings (
						key TEXT PRIMARY KEY,
						value TEXT NOT NULL
					);
				";
				await cmd.ExecuteNonQueryAsync();
			}

			try {
				using var migration = db.CreateCommand();
				migration.CommandText = "ALTER TABLE player_blacklist ADD COLUMN player_id INTEGER NULL;";
				await migration.ExecuteNonQueryAsync();
			} catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) {
				// Existing databases already have the column.
			}

			var cache = new FarmTargetCache(db);

			// Load persisted settings (MinimumResources, etc.)
			await cache.LoadSettings();

			if (isNewDatabase) {
				await cache.ImportLegacyJsonCacheIfPresent(GetLegacyCacheFilePath(instanceSettingsPath, instanceAlias));
			}

			using (var cmd = db.CreateCommand()) {
				cmd.CommandText = "SELECT entry_json FROM farm_target_cache";
				using var reader = await cmd.ExecuteReaderAsync();
				while (await reader.ReadAsync()) {
					try {
						var entry = JsonConvert.DeserializeObject<FarmTargetCacheEntry>(reader.GetString(0));
						if (entry != null)
							cache._entries[GetKey(entry.Coordinate)] = entry;
					} catch {
						// Corrupt row - skip it rather than fail the whole load.
					}
				}
			}

			return cache;
		}

		private async Task ImportLegacyJsonCacheIfPresent(string legacyFilePath) {
			if (!File.Exists(legacyFilePath)) return;
			try {
				var json = await File.ReadAllTextAsync(legacyFilePath);
				var entries = JsonConvert.DeserializeObject<List<FarmTargetCacheEntry>>(json) ?? new();
				foreach (var entry in entries) {
					await UpsertAndPersist(entry);
				}
			} catch {
				// Legacy file unreadable/corrupt - start fresh in SQLite rather than fail the load.
			}
		}

		private async Task LoadSettings() {
			try {
				using var cmd = _db.CreateCommand();
				cmd.CommandText = "SELECT value FROM farm_settings WHERE key = 'MinimumResources'";
				var result = await cmd.ExecuteScalarAsync();
				if (result != null && long.TryParse((string)result, out var minResources)) {
					MinimumResources = minResources;
				}
			} catch {
				// Settings table might not exist yet on first load, or value is invalid - use default
			}
		}

		public async Task SaveSettings() {
			try {
				using var cmd = _db.CreateCommand();
				cmd.CommandText = @"
					INSERT INTO farm_settings (key, value)
					VALUES ('MinimumResources', $value)
					ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
				cmd.Parameters.AddWithValue("$value", MinimumResources.ToString());
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort persistence
			}
		}

		/// <summary>Prunes entries seen only in galaxy scans that are older than ReportRetentionDays; entries with a report or marked inactive are never pruned (unlike KeepReportFor, which times out espionage reports for re-probing).</summary>
		public async Task Save() {
			var cutoff = DateTime.UtcNow.AddDays(-ReportRetentionDays);
			var staleKeys = _entries.Values
				.Where(e => e.LastSeenDate < cutoff 
					&& e.LastReportDate == null 
					&& !e.IsInactive) // NEVER prune inactive targets - eternal persistence
				.Select(e => GetKey(e.Coordinate))
				.ToList();

			foreach (var key in staleKeys) {
				_entries.Remove(key);
				using var cmd = _db.CreateCommand();
				cmd.CommandText = "DELETE FROM farm_target_cache WHERE coordinate_key = $key";
				cmd.Parameters.AddWithValue("$key", key);
				await cmd.ExecuteNonQueryAsync();
			}
		}

		public async Task Upsert(FarmTargetCacheEntry entry) {
			_entries[GetKey(entry.Coordinate)] = entry;
			// Awaited, since the connection is disposed and replaced at the start of the next cycle and a write still in flight would race that Dispose().
			await UpsertAndPersist(entry);
		}

		private async Task UpsertAndPersist(FarmTargetCacheEntry entry) {
			try {
				using var cmd = _db.CreateCommand();
				cmd.CommandText = @"
					INSERT INTO farm_target_cache (coordinate_key, galaxy, system, last_seen_date, last_report_date, entry_json)
					VALUES ($key, $galaxy, $system, $lastSeen, $lastReport, $json)
					ON CONFLICT(coordinate_key) DO UPDATE SET
						galaxy = excluded.galaxy,
						system = excluded.system,
						last_seen_date = excluded.last_seen_date,
						last_report_date = excluded.last_report_date,
						entry_json = excluded.entry_json;
				";
				cmd.Parameters.AddWithValue("$key", GetKey(entry.Coordinate));
				cmd.Parameters.AddWithValue("$galaxy", entry.Coordinate.Galaxy);
				cmd.Parameters.AddWithValue("$system", entry.Coordinate.System);
				cmd.Parameters.AddWithValue("$lastSeen", entry.LastSeenDate.ToString("O"));
				cmd.Parameters.AddWithValue("$lastReport", (object) entry.LastReportDate?.ToString("O") ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$json", JsonConvert.SerializeObject(entry));
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort persistence - the in-memory read cache already has the up-to-date value,
				// so a transient DB write failure doesn't lose data for the rest of this run.
			}
		}

		public FarmTargetCacheEntry Get(Coordinate coord) {
			return _entries.TryGetValue(GetKey(coord), out var entry) ? entry : null;
		}

		/// <summary>Gets the cached entries in a galaxy/system range that meet the minimum resources threshold, for FastFarmMode and ScanOnly.</summary>
		public List<FarmTargetCacheEntry> GetInRange(int galaxy, int startSystem, int endSystem) {
			return _entries.Values
				.Where(e => e.Coordinate.Galaxy == galaxy && e.Coordinate.System >= startSystem && e.Coordinate.System <= endSystem)
				.Where(e => e.LastKnownResources != null && e.LastKnownResources.TotalResources >= MinimumResources)
				.ToList();
		}

		/// <summary>Gets the cached entries in a galaxy/system range without the minimum resources filter, for debugging and cache management.</summary>
		public List<FarmTargetCacheEntry> GetAllInRange(int galaxy, int startSystem, int endSystem) {
			return _entries.Values
				.Where(e => e.Coordinate.Galaxy == galaxy && e.Coordinate.System >= startSystem && e.Coordinate.System <= endSystem)
				.ToList();
		}

		/// <summary>Highest known stockpile among the still-eligible cached targets in this system, or 0, so FastFarmMode visits the most promising systems first.</summary>
		public long GetBestKnownResourcesInSystem(int galaxy, int system) {
			var candidates = _entries.Values
				.Where(e => e.Coordinate.Galaxy == galaxy && e.Coordinate.System == system)
				.Where(e => e.IsInactive && !e.IsAdministrator && !e.IsBanned && !e.IsVacation)
				.Where(e => e.LastKnownResources != null && e.LastKnownResources.TotalResources >= MinimumResources);
			long best = 0;
			foreach (var entry in candidates) {
				var total = entry.LastKnownResources.TotalResources;
				if (total > best)
					best = total;
			}
			return best;
		}

		// --- System scan cache (FastFarmMode: lets AutoFarm skip a live galaxy scan for a system
		// it already scanned recently, reusing what a normal AutoFarm cycle found instead) ---

		public void MarkSystemScanned(int galaxy, int system) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO system_scan_cache (galaxy, system, scanned_at)
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
			cmd.CommandText = "SELECT scanned_at FROM system_scan_cache WHERE galaxy = $galaxy AND system = $system";
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$system", system);
			var result = cmd.ExecuteScalar();
			if (result == null) return null;
			var scannedAt = DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
			return DateTime.UtcNow - scannedAt;
		}

		// --- Blacklist (P20: replaces AutoFarmWorker's in-memory-only _farmBlacklist dictionary) ---

		public bool IsBlacklisted(Coordinate coord, out DateTime blacklistedUntil) {
			blacklistedUntil = default;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT blacklisted_until FROM farm_blacklist WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			var result = cmd.ExecuteScalar();
			if (result == null) return false;
			blacklistedUntil = DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
			return true;
		}

		public void Blacklist(Coordinate coord, DateTime until) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO farm_blacklist (coordinate_key, blacklisted_until)
				VALUES ($key, $until)
				ON CONFLICT(coordinate_key) DO UPDATE SET blacklisted_until = excluded.blacklisted_until;
			";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			cmd.Parameters.AddWithValue("$until", until.ToString("O"));
			cmd.ExecuteNonQuery();
		}

		public void RemoveFromBlacklist(Coordinate coord) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "DELETE FROM farm_blacklist WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			cmd.ExecuteNonQuery();
		}

		// Player blacklist: the player_id is stored and checked first when the caller has it, so a renamed player stays listed; callers without an id still use the name-only path.

		public bool IsPlayerBlacklisted(string playerName, out DateTime blacklistedUntil, int playerId = 0) {
			blacklistedUntil = default;
			if (playerId > 0) {
				using var idCmd = _db.CreateCommand();
				idCmd.CommandText = "SELECT blacklisted_until FROM player_blacklist WHERE player_id = $id";
				idCmd.Parameters.AddWithValue("$id", playerId);
				var idResult = idCmd.ExecuteScalar();
				if (idResult != null) {
					blacklistedUntil = DateTime.Parse((string) idResult, null, System.Globalization.DateTimeStyles.RoundtripKind);
					return true;
				}
			}
			if (string.IsNullOrEmpty(playerName)) return false;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT blacklisted_until FROM player_blacklist WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			var result = cmd.ExecuteScalar();
			if (result == null) return false;
			blacklistedUntil = DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
			return true;
		}

		public void BlacklistPlayer(string playerName, DateTime until, int playerId = 0) {
			if (string.IsNullOrEmpty(playerName)) return;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO player_blacklist (player_name, blacklisted_until, player_id)
				VALUES ($name, $until, $id)
				ON CONFLICT(player_name) DO UPDATE SET blacklisted_until = excluded.blacklisted_until, player_id = excluded.player_id;
			";
			cmd.Parameters.AddWithValue("$name", playerName);
			cmd.Parameters.AddWithValue("$until", until.ToString("O"));
			cmd.Parameters.AddWithValue("$id", playerId > 0 ? (object) playerId : DBNull.Value);
			cmd.ExecuteNonQuery();
		}

		public void RemoveFromPlayerBlacklist(string playerName) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "DELETE FROM player_blacklist WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			cmd.ExecuteNonQuery();
		}

		// --- Player activity tracking (keyed by PlayerId so an inactive who resettles/reactivates
		// on a different planet is recognized immediately; avoids wasting fleets/probes on active players) ---

		public bool IsPlayerActive(int playerId, out DateTime lastChecked, out int? activityDays) {
			lastChecked = default;
			activityDays = null;
			if (playerId <= 0) return false;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT is_active, last_checked, activity_days FROM player_activity WHERE player_id = $id";
			cmd.Parameters.AddWithValue("$id", playerId);
			using var reader = cmd.ExecuteReader();
			if (!reader.Read()) return false;
			lastChecked = DateTime.Parse((string) reader["last_checked"], null, System.Globalization.DateTimeStyles.RoundtripKind);
			activityDays = reader["activity_days"] == DBNull.Value ? null : Convert.ToInt32(reader["activity_days"]);
			return Convert.ToInt32(reader["is_active"]) == 1;
		}

		public void SetPlayerActivity(int playerId, string playerName, bool isActive, int? activityDays) {
			if (playerId <= 0 || string.IsNullOrEmpty(playerName)) return;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO player_activity (player_id, player_name, is_active, last_checked, activity_days)
				VALUES ($id, $name, $active, $checked, $days)
				ON CONFLICT(player_id) DO UPDATE SET
					player_name = excluded.player_name,
					is_active = excluded.is_active,
					last_checked = excluded.last_checked,
					activity_days = excluded.activity_days;
			";
			cmd.Parameters.AddWithValue("$id", playerId);
			cmd.Parameters.AddWithValue("$name", playerName);
			cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0);
			cmd.Parameters.AddWithValue("$checked", DateTime.UtcNow.ToString("O"));
			cmd.Parameters.AddWithValue("$days", activityDays.HasValue ? (object) activityDays.Value : DBNull.Value);
			cmd.ExecuteNonQuery();
		}

		public List<int> GetActivePlayerIds() {
			var ids = new List<int>();
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT player_id FROM player_activity WHERE is_active = 1";
			using var reader = cmd.ExecuteReader();
			while (reader.Read()) {
				ids.Add(Convert.ToInt32(reader["player_id"]));
			}
			return ids;
		}

		// --- Attack history (#8: real loot collected per coordinate, used both to feed the weighted
		// score (#6) and to decide poor-farm blacklisting by real results instead of just estimates) ---

		public async Task RecordAttack(Coordinate coord, string playerName, long metal, long crystal, long deuterium, DateTime attackedAt) {
			try {
				using var cmd = _db.CreateCommand();
				cmd.CommandText = @"
					INSERT INTO attack_history (coordinate_key, galaxy, system, position, player_name, metal, crystal, deuterium, attacked_at)
					VALUES ($key, $galaxy, $system, $position, $playerName, $metal, $crystal, $deuterium, $attackedAt);
				";
				cmd.Parameters.AddWithValue("$key", GetKey(coord));
				cmd.Parameters.AddWithValue("$galaxy", coord.Galaxy);
				cmd.Parameters.AddWithValue("$system", coord.System);
				cmd.Parameters.AddWithValue("$position", coord.Position);
				cmd.Parameters.AddWithValue("$playerName", string.IsNullOrEmpty(playerName) ? (object) DBNull.Value : playerName);
				cmd.Parameters.AddWithValue("$metal", metal);
				cmd.Parameters.AddWithValue("$crystal", crystal);
				cmd.Parameters.AddWithValue("$deuterium", deuterium);
				cmd.Parameters.AddWithValue("$attackedAt", attackedAt.ToString("O"));
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort - a failed history write must never abort the attack flow that triggered it.
			}
		}

		/// <summary>Average real total loot (metal+crystal+deuterium) collected from this coordinate across its recorded raids, or null if never recorded.</summary>
		public double? GetAverageLoot(Coordinate coord) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT AVG(metal + crystal + deuterium) FROM attack_history WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			var result = cmd.ExecuteScalar();
			return result == null || result == DBNull.Value ? null : Convert.ToDouble(result);
		}

		/// <summary>Number of recorded raids against this coordinate.</summary>
		public int GetAttackCount(Coordinate coord) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM attack_history WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			return Convert.ToInt32(cmd.ExecuteScalar());
		}

		/// <summary>Average real total loot collected across all recorded raids against this player (any coordinate), or null if never recorded. Used for poor-farm blacklisting by player (#10) - recognizes an inactive even after they resettle on a different planet.</summary>
		public double? GetAveragePlayerLoot(string playerName) {
			if (string.IsNullOrEmpty(playerName)) return null;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT AVG(metal + crystal + deuterium) FROM attack_history WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			var result = cmd.ExecuteScalar();
			return result == null || result == DBNull.Value ? null : Convert.ToDouble(result);
		}

		public int GetPlayerAttackCount(string playerName) {
			if (string.IsNullOrEmpty(playerName)) return 0;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM attack_history WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			return Convert.ToInt32(cmd.ExecuteScalar());
		}

		public void Dispose() {
			_db?.Dispose();
		}
	}
}
