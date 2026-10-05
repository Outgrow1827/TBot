using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TBot.Model {
	public sealed class FeatureResultEntry {
		public int Id { get; set; }
		public string Coordinate { get; set; }
		public string Summary { get; set; }
		public string Resources { get; set; }
		public string Ships { get; set; }
		public DateTime TimestampUtc { get; set; }
		// Raw numeric resource totals, kept alongside the display-formatted Resources string above
		// (e.g. "1.23M/456K/78D") so consumers - the dashboard's grand-total sum - don't need to
		// re-parse the formatted string back into numbers.
		public long Metal { get; set; }
		public long Crystal { get; set; }
		public long Deuterium { get; set; }
		public long Darkmatter { get; set; }
		// Only populated for AutoDiscovery results, which never carry Resources/Ships - the WebUI
		// dashboard uses this to show a dedicated "Artifacts/Lifeform" column instead of the two
		// always-empty ones (confirmed live 2026-08-29: user pointed out Resources/Ships are dead
		// weight on the AutoDiscovery page since discoveries can't produce either).
		public string ArtifactsOrLifeform { get; set; }
		// Raw artifact count, kept alongside the display string above so the grand-total panel can
		// sum it - artifacts aren't Metal/Crystal/Deuterium/Darkmatter, so the resource total alone
		// is always zero for AutoDiscovery (confirmed live 2026-08-29, user pointed out artifacts
		// "não colocados no somatório").
		public long ArtifactsFound { get; set; }
	}

	// Structured, WebUI-facing counterpart to logging each result line - the log/CSV is meant for
	// worker diagnostics, not a dump of every expedition/discovery narrative text (confirmed live
	// 2026-08-29: dozens of near-duplicate flavor-text lines per cycle made the log file itself
	// hard to read). Same JSON-file-in-data/ pattern as AutoDiscoveryCursorStore.
	public sealed class FeatureResultsStore {
		private const int MaxEntries = 200;
		private readonly object _lock = new();
		private readonly string _filePath;
		private List<FeatureResultEntry> _entries = new();

		public FeatureResultsStore(string fileName) {
			var dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			Directory.CreateDirectory(dataFolder);
			_filePath = Path.Combine(dataFolder, Path.GetFileName(fileName));
			Load();
		}

		// Snapshot of everything currently held (up to MaxEntries) - used to backfill the permanent
		// SQLite store (FeatureResultsSqliteStore) with whatever this capped JSON file still has on
		// hand, requested live 2026-09-02 after the user lost history to a bug that predates the
		// permanent store existing at all. Doesn't reach further back than the 200-entry cap already
		// allowed - there's no way to recover what had already been evicted before today.
		public IReadOnlyList<FeatureResultEntry> GetAll() {
			lock (_lock) {
				return _entries.ToList();
			}
		}

		public void Add(FeatureResultEntry entry) {
			if (entry == null)
				return;

			lock (_lock) {
				// Id-based dedup: the worker's own in-memory "already logged" set resets on every
				// restart (settings reload, crash recovery, etc.), which happens far more often than
				// expected in practice - confirmed live 2026-08-29 that a single discovery_results
				// store had 200 entries covering only 57 distinct message IDs, each duplicated ~3x,
				// silently evicting genuinely distinct older results out of the MaxEntries cap.
				if (_entries.Any(e => e.Id == entry.Id))
					return;
				_entries.Add(entry);
				if (_entries.Count > MaxEntries)
					_entries = _entries.OrderByDescending(e => e.TimestampUtc).Take(MaxEntries).ToList();
				Persist();
			}
		}

		private void Load() {
			if (!File.Exists(_filePath))
				return;

			try {
				var loaded = JsonConvert.DeserializeObject<List<FeatureResultEntry>>(File.ReadAllText(_filePath));
				if (loaded != null)
					_entries = loaded;
			} catch {
				_entries = new List<FeatureResultEntry>();
			}
		}

		private void Persist() {
			try {
				var tempPath = _filePath + ".tmp";
				File.WriteAllText(tempPath, JsonConvert.SerializeObject(_entries, Formatting.Indented));

				if (File.Exists(_filePath)) {
					try {
						File.Replace(tempPath, _filePath, null);
					} catch {
						File.Delete(_filePath);
						File.Move(tempPath, _filePath);
					}
				} else {
					File.Move(tempPath, _filePath);
				}
			} catch {
				// Results persistence must never stop the worker.
			}
		}
	}
}
