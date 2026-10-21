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
		// Raw totals kept next to the formatted string so the dashboard's grand total doesn't re-parse it.
		public long Metal { get; set; }
		public long Crystal { get; set; }
		public long Deuterium { get; set; }
		public long Darkmatter { get; set; }
		// Only AutoDiscovery results fill this; the dashboard shows it instead of the always-empty Resources/Ships columns.
		public string ArtifactsOrLifeform { get; set; }
		// Raw artifact count kept next to the display string, since artifacts never appear in the resource total.
		public long ArtifactsFound { get; set; }
	}

	// Structured, WebUI-facing counterpart to the result log lines; same JSON-file-in-data/ pattern as AutoDiscoveryCursorStore.
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

		// Snapshot of the capped entries, used to backfill the permanent SQLite store; it can't reach further back than the 200-entry cap.
		public IReadOnlyList<FeatureResultEntry> GetAll() {
			lock (_lock) {
				return _entries.ToList();
			}
		}

		public void Add(FeatureResultEntry entry) {
			if (entry == null)
				return;

			lock (_lock) {
				// Id-based dedup, because the worker's in-memory "already logged" set resets on every restart and duplicates evicted distinct results.
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
