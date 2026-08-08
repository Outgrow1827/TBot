using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Tbot.Workers;

namespace TBot.Model {
	/// <summary>
	/// Persists AutoDiscovery cursors per instance so a worker restart resumes from
	/// the last examined position instead of probing the same system again.
	/// </summary>
	public sealed class AutoDiscoveryCursorStore {
		private sealed class CursorEntry {
			public int System { get; set; }
			public int NextPosition { get; set; }
		}

		private readonly object _lock = new();
		private readonly string _filePath;
		private Dictionary<string, CursorEntry> _entries = new(StringComparer.Ordinal);

		public AutoDiscoveryCursorStore(string fileName) {
			var dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			Directory.CreateDirectory(dataFolder);
			_filePath = Path.Combine(dataFolder, Path.GetFileName(fileName));
			Load();
		}

		public DiscoveryCursorState Load(string key, int fallbackSystem) {
			lock (_lock) {
				if (!_entries.TryGetValue(key, out var entry))
					return new DiscoveryCursorState(fallbackSystem);

				return new DiscoveryCursorState(entry.System, entry.NextPosition);
			}
		}

		public void Save(string key, DiscoveryCursorState cursor) {
			if (string.IsNullOrWhiteSpace(key) || cursor == null)
				return;

			lock (_lock) {
				_entries[key] = new CursorEntry {
					System = cursor.System,
					NextPosition = cursor.NextPosition
				};
				Persist();
			}
		}

		private void Load() {
			if (!File.Exists(_filePath))
				return;

			try {
				var loaded = JsonConvert.DeserializeObject<Dictionary<string, CursorEntry>>(
					File.ReadAllText(_filePath));
				if (loaded != null)
					_entries = new Dictionary<string, CursorEntry>(loaded, StringComparer.Ordinal);
			} catch {
				_entries = new Dictionary<string, CursorEntry>(StringComparer.Ordinal);
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
				// Cursor persistence must never stop discovery planning.
			}
		}
	}
}
