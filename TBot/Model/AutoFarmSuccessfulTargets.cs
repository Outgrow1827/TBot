using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model
{
	public class SuccessfulTarget
	{
		public string Coordinate { get; set; }
		public DateTime FirstAttacked { get; set; }
		public DateTime LastAttacked { get; set; }
		public int TimesAttacked { get; set; }
		public Resources TotalLootCollected { get; set; }
		public Resources AverageLoot { get; set; }

		public SuccessfulTarget()
		{
			TotalLootCollected = new Resources();
			AverageLoot = new Resources();
		}

		public SuccessfulTarget(string coordinate, Resources loot)
		{
			Coordinate = coordinate;
			FirstAttacked = DateTime.UtcNow;
			LastAttacked = DateTime.UtcNow;
			TimesAttacked = 1;

			TotalLootCollected = new Resources
			{
				Metal = loot.Metal,
				Crystal = loot.Crystal,
				Deuterium = loot.Deuterium
			};

			AverageLoot = new Resources
			{
				Metal = loot.Metal,
				Crystal = loot.Crystal,
				Deuterium = loot.Deuterium
			};
		}

		public void AddAttack(Resources loot)
		{
			LastAttacked = DateTime.UtcNow;
			TimesAttacked++;

			TotalLootCollected.Metal += loot.Metal;
			TotalLootCollected.Crystal += loot.Crystal;
			TotalLootCollected.Deuterium += loot.Deuterium;

			AverageLoot.Metal = TotalLootCollected.Metal / TimesAttacked;
			AverageLoot.Crystal = TotalLootCollected.Crystal / TimesAttacked;
			AverageLoot.Deuterium = TotalLootCollected.Deuterium / TimesAttacked;
		}
	}

	public class AutoFarmSuccessfulTargets
	{
		private List<SuccessfulTarget> _successfulTargets;
		private readonly object _lock = new object();

		private string _filePath;

		private DateTime _lastSaveUtc = DateTime.MinValue;
		private int _pendingChanges = 0;

		private readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(60);
		private const int SaveAfterChanges = 20;

		private const int MaxTargets = 5000;
		private readonly TimeSpan _staleCutoff = TimeSpan.FromDays(30);

		public AutoFarmSuccessfulTargets()
		{
			_successfulTargets = new List<SuccessfulTarget>();
		}

		public AutoFarmSuccessfulTargets(string fileName)
		{
			string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			if (!Directory.Exists(dataFolder))
			{
				Directory.CreateDirectory(dataFolder);
			}

			string safeFileName = MakeSafeFileName(fileName);
			_filePath = Path.Combine(dataFolder, safeFileName);

			_successfulTargets = new List<SuccessfulTarget>();
			LoadFromFile();
		}

		public void RecordAttack(Coordinate coordinate, Resources loot)
		{
			lock (_lock)
			{
				string coordStr = $"{coordinate.Galaxy}:{coordinate.System}:{coordinate.Position}";

				var existing = _successfulTargets.FirstOrDefault(t => t.Coordinate == coordStr);
				if (existing != null)
				{
					existing.AddAttack(loot);
				}
				else
				{
					_successfulTargets.Add(new SuccessfulTarget(coordStr, loot));
				}

				_pendingChanges++;

				CleanupUnsafeGrowth_NoThrow();

				SaveIfNeeded_NoThrow();
			}
		}

		public SuccessfulTarget GetTarget(Coordinate coordinate)
		{
			lock (_lock)
			{
				string coordStr = $"{coordinate.Galaxy}:{coordinate.System}:{coordinate.Position}";
				return _successfulTargets.FirstOrDefault(t => t.Coordinate == coordStr);
			}
		}

		public List<SuccessfulTarget> GetAllTargets()
		{
			lock (_lock)
			{
				return new List<SuccessfulTarget>(_successfulTargets);
			}
		}

		public int GetTotalCount()
		{
			lock (_lock)
			{
				return _successfulTargets.Count;
			}
		}

		public long GetTotalLootCollected()
		{
			lock (_lock)
			{
				return _successfulTargets.Sum(t =>
					t.TotalLootCollected.Metal +
					t.TotalLootCollected.Crystal +
					t.TotalLootCollected.Deuterium
				);
			}
		}


		public void ForceSave()
		{
			lock (_lock)
			{
				SaveToFile_NoThrow(force: true);
			}
		}

		private void LoadFromFile()
		{
			if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
			{
				return;
			}

			try
			{
				string json = File.ReadAllText(_filePath);
				var loaded = JsonConvert.DeserializeObject<List<SuccessfulTarget>>(json);
				if (loaded != null)
				{
					_successfulTargets = loaded;
				}
			}
			catch (Exception ex)
			{
				_successfulTargets = new List<SuccessfulTarget>();
				WriteError_NoThrow($"LoadFromFile failed: {ex.GetType().Name}: {ex.Message}");
			}
		}

		private void SaveIfNeeded_NoThrow()
		{
			if (string.IsNullOrEmpty(_filePath))
				return;

			var now = DateTime.UtcNow;

			bool timeDue = (now - _lastSaveUtc) >= _saveInterval;
			bool changesDue = _pendingChanges >= SaveAfterChanges;

			if (timeDue || changesDue)
			{
				SaveToFile_NoThrow(force: false);
			}
		}

		private void SaveToFile_NoThrow(bool force)
		{
			if (string.IsNullOrEmpty(_filePath))
				return;

			try
			{
				if (!force && _pendingChanges <= 0)
					return;

				string json = JsonConvert.SerializeObject(_successfulTargets, Formatting.Indented);

				string tmpPath = _filePath + ".tmp";

				File.WriteAllText(tmpPath, json);

				if (File.Exists(_filePath))
				{
					try
					{
						File.Replace(tmpPath, _filePath, null);
					}
					catch
					{
						File.Delete(_filePath);
						File.Move(tmpPath, _filePath);
					}
				}
				else
				{
					File.Move(tmpPath, _filePath);
				}

				_lastSaveUtc = DateTime.UtcNow;
				_pendingChanges = 0;
			}
			catch (Exception ex)
			{
				WriteError_NoThrow($"SaveToFile failed: {ex.GetType().Name}: {ex.Message}");
			}
		}

		private void CleanupUnsafeGrowth_NoThrow()
		{
			try
			{
				var cutoff = DateTime.UtcNow - _staleCutoff;
				_successfulTargets.RemoveAll(t => t.LastAttacked < cutoff);

				if (_successfulTargets.Count > MaxTargets)
				{
					_successfulTargets = _successfulTargets
						.OrderByDescending(t => t.LastAttacked)
						.Take(MaxTargets)
						.ToList();
				}
			}
			catch (Exception ex)
			{
				WriteError_NoThrow($"Cleanup failed: {ex.GetType().Name}: {ex.Message}");
			}
		}

		private static string MakeSafeFileName(string fileName)
		{
			if (string.IsNullOrWhiteSpace(fileName))
				return "autofarm_successful_targets.json";

			string name = Path.GetFileName(fileName);

			if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
				name += ".json";

			foreach (char c in Path.GetInvalidFileNameChars())
			{
				name = name.Replace(c, '_');
			}

			return name;
		}

		private void WriteError_NoThrow(string message)
		{
			try
			{
				string baseDir = Path.GetDirectoryName(_filePath);
				if (string.IsNullOrEmpty(baseDir))
					return;

				string errPath = Path.Combine(baseDir, "autofarm_successful_targets.errors.log");
				string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {message}{Environment.NewLine}";
				File.AppendAllText(errPath, line);
			}
			catch
			{

			}
		}
	}
}
