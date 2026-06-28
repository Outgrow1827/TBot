using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	/// <summary>
	/// Simple JSON-file-backed persistence for FarmTargetCacheEntry, scoped to AutoFarmWorker.
	/// Lets FastFarmMode reuse previously-discovered inactive targets across bot restarts
	/// instead of always re-scanning the galaxy live.
	/// </summary>
	public class FarmTargetCache {
		private readonly Dictionary<string, FarmTargetCacheEntry> _entries = new();
		private readonly string _filePath;

		private FarmTargetCache(string filePath) {
			_filePath = filePath;
		}

		private static string GetKey(Coordinate coord) {
			return $"{coord.Galaxy}:{coord.System}:{coord.Position}:{coord.Type}";
		}

		public static string GetCacheFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			return Path.Combine(directory ?? ".", $"fastfarm_cache_{instanceAlias}.json");
		}

		public static async Task<FarmTargetCache> Load(string instanceSettingsPath, string instanceAlias) {
			var filePath = GetCacheFilePath(instanceSettingsPath, instanceAlias);
			var cache = new FarmTargetCache(filePath);
			if (File.Exists(filePath)) {
				try {
					var json = await File.ReadAllTextAsync(filePath);
					var entries = JsonConvert.DeserializeObject<List<FarmTargetCacheEntry>>(json) ?? new();
					foreach (var entry in entries) {
						cache._entries[GetKey(entry.Coordinate)] = entry;
					}
				} catch {
					// Corrupted or unreadable cache file: start fresh rather than crash the worker.
				}
			}
			return cache;
		}

		public async Task Save() {
			var cutoff = DateTime.UtcNow.AddDays(-7);
			var staleKeys = _entries.Values
				.Where(e => e.LastSeenDate < cutoff && e.LastReportDate == null)
				.Select(e => GetKey(e.Coordinate))
				.ToList();
			foreach (var key in staleKeys)
				_entries.Remove(key);

			var json = JsonConvert.SerializeObject(_entries.Values.ToList(), Formatting.Indented);
			await File.WriteAllTextAsync(_filePath, json);
		}

		public void Upsert(FarmTargetCacheEntry entry) {
			_entries[GetKey(entry.Coordinate)] = entry;
		}

		public FarmTargetCacheEntry Get(Coordinate coord) {
			return _entries.TryGetValue(GetKey(coord), out var entry) ? entry : null;
		}

		public List<FarmTargetCacheEntry> GetInRange(int galaxy, int startSystem, int endSystem) {
			return _entries.Values
				.Where(e => e.Coordinate.Galaxy == galaxy && e.Coordinate.System >= startSystem && e.Coordinate.System <= endSystem)
				.ToList();
		}
	}
}
