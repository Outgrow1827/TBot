using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TBot.Model {
	/// <summary>
	/// Remembers systems that were scanned successfully but contained no eligible
	/// farm target. The memory is persisted so a worker restart does not immediately
	/// rescan the same empty systems.
	/// </summary>
	public sealed class AutoFarmEmptySystemCache {
		private sealed class EmptySystem {
			public int Galaxy { get; set; }
			public int System { get; set; }
			public DateTime ExpiresAtUtc { get; set; }
		}

		private readonly object _lock = new();
		private readonly string _filePath;
		private List<EmptySystem> _systems = new();

		public AutoFarmEmptySystemCache() {
		}

		public AutoFarmEmptySystemCache(string fileName) {
			var dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			Directory.CreateDirectory(dataFolder);
			_filePath = Path.Combine(dataFolder, Path.GetFileName(fileName));
			Load();
		}

		public bool IsCoolingDown(int galaxy, int system, DateTime? nowUtc = null) {
			lock (_lock) {
				var now = nowUtc ?? DateTime.UtcNow;
				var changed = RemoveExpired(now);
				if (changed)
					Save();

				return _systems.Any(entry =>
					entry.Galaxy == galaxy &&
					entry.System == system &&
					entry.ExpiresAtUtc > now);
			}
		}

		public void MarkEmpty(int galaxy, int system, TimeSpan cooldown, DateTime? nowUtc = null) {
			lock (_lock) {
				var now = nowUtc ?? DateTime.UtcNow;
				_systems.RemoveAll(entry => entry.Galaxy == galaxy && entry.System == system);
				_systems.Add(new EmptySystem {
					Galaxy = galaxy,
					System = system,
					ExpiresAtUtc = now.Add(cooldown)
				});
				Save();
			}
		}

		public void MarkNonEmpty(int galaxy, int system) {
			lock (_lock) {
				if (_systems.RemoveAll(entry => entry.Galaxy == galaxy && entry.System == system) > 0)
					Save();
			}
		}

		private bool RemoveExpired(DateTime nowUtc) {
			return _systems.RemoveAll(entry => entry.ExpiresAtUtc <= nowUtc) > 0;
		}

		private void Load() {
			if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
				return;

			try {
				var loaded = JsonConvert.DeserializeObject<List<EmptySystem>>(File.ReadAllText(_filePath));
				if (loaded == null)
					return;

				lock (_lock) {
					_systems = loaded;
					if (RemoveExpired(DateTime.UtcNow))
						Save();
				}
			} catch {
				_systems = new List<EmptySystem>();
			}
		}

		private void Save() {
			if (string.IsNullOrEmpty(_filePath))
				return;

			try {
				var json = JsonConvert.SerializeObject(_systems, Formatting.Indented);
				var tempPath = _filePath + ".tmp";
				File.WriteAllText(tempPath, json);

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
				// A cache failure must never stop AutoFarm from working.
			}
		}
	}
}
