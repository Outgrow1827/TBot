using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Model
{

	public class FarmBotInstance
	{
		public string Name { get; set; }
		public DateTime LastHeartbeat { get; set; }
		public bool IsActive { get; set; }

		public bool IsAlive(TimeSpan timeout)
		{
			return (DateTime.UtcNow - LastHeartbeat) < timeout;
		}
	}

	public class ScannedTarget
	{
		public string Coord { get; set; }
		public string ScannedBy { get; set; }
		public DateTime ScannedAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public long TotalResources { get; set; }
		public bool HasFleet { get; set; }
		public bool HasDefense { get; set; }

		public bool IsExpired()
		{
			return DateTime.UtcNow >= ExpiresAt;
		}

		public Coordinate GetCoordinate()
		{
			if (string.IsNullOrWhiteSpace(Coord))
				return null;

			var parts = Coord.Split(':');
			if (parts.Length != 3)
				return null;

			if (!int.TryParse(parts[0], out var g)) return null;
			if (!int.TryParse(parts[1], out var s)) return null;
			if (!int.TryParse(parts[2], out var p)) return null;

			return new Coordinate
			{
				Galaxy = g,
				System = s,
				Position = p,
				Type = Celestials.Planet
			};
		}
	}

	public class ClaimedAttack
	{
		public string Coord { get; set; }
		public string ClaimedBy { get; set; }
		public DateTime ClaimedAt { get; set; }
		public DateTime? AttackSentAt { get; set; }
		public DateTime ReturnsAt { get; set; }

		public bool IsExpired()
		{
			if (DateTime.UtcNow >= ReturnsAt)
				return true;

			if (!AttackSentAt.HasValue && (DateTime.UtcNow - ClaimedAt).TotalHours > 2)
				return true;

			return false;
		}

		public Coordinate GetCoordinate()
		{
			if (string.IsNullOrWhiteSpace(Coord))
				return null;

			var parts = Coord.Split(':');
			if (parts.Length != 3)
				return null;

			if (!int.TryParse(parts[0], out var g)) return null;
			if (!int.TryParse(parts[1], out var s)) return null;
			if (!int.TryParse(parts[2], out var p)) return null;

			return new Coordinate
			{
				Galaxy = g,
				System = s,
				Position = p,
				Type = Celestials.Planet
			};
		}
	}

	public class SharedFarmState
	{
		private const string DEFAULT_FILENAME = "shared_farm_state.json";
		private const int MAX_RETRY_ATTEMPTS = 5;
		private const int RETRY_DELAY_MS = 100;
		private const string MUTEX_GLOBAL_NAME = @"Global\TBot_SharedFarmState_Mutex";
		private const string MUTEX_LOCAL_NAME = @"Local\TBot_SharedFarmState_Mutex";

		public string Version { get; set; } = "1.1";
		public DateTime LastUpdated { get; set; }
		public List<FarmBotInstance> Instances { get; set; }
		public List<ScannedTarget> ScannedTargets { get; set; }
		public List<ClaimedAttack> ClaimedAttacks { get; set; }

		private static readonly object _globalLock = new object();
		private string _filePath;

		public SharedFarmState()
		{
			Instances = new List<FarmBotInstance>();
			ScannedTargets = new List<ScannedTarget>();
			ClaimedAttacks = new List<ClaimedAttack>();
			LastUpdated = DateTime.UtcNow;
		}

		public static SharedFarmState Initialize(string instanceName = null)
		{
			string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			if (!Directory.Exists(dataFolder))
				Directory.CreateDirectory(dataFolder);

			string filePath = Path.Combine(dataFolder, DEFAULT_FILENAME);
			var state = LoadFromFile(filePath);
			state._filePath = filePath;

			if (!string.IsNullOrEmpty(instanceName))
			{
				state.UpdateInstanceHeartbeat(instanceName);
				state.Save();
			}

			return state;
		}

		private static SharedFarmState LoadFromFile(string filePath)
		{
			lock (_globalLock)
			{
				if (!File.Exists(filePath))
					return new SharedFarmState();

				for (int attempt = 0; attempt < MAX_RETRY_ATTEMPTS; attempt++)
				{
					try
					{
						string json = File.ReadAllText(filePath);
						var state = JsonConvert.DeserializeObject<SharedFarmState>(json);

						if (state != null)
						{
							state._filePath = filePath;
							state.CleanupExpiredEntries();
							return state;
						}
					}
					catch (IOException) when (attempt < MAX_RETRY_ATTEMPTS - 1)
					{
						Thread.Sleep(RETRY_DELAY_MS * (attempt + 1));
					}
					catch (JsonException)
					{
						try
						{
							string backupPath = filePath + ".corrupt." + DateTime.UtcNow.Ticks;
							File.Move(filePath, backupPath);
						}
						catch {}
						return new SharedFarmState();
					}
					catch
					{
						return new SharedFarmState();
					}
				}

				return new SharedFarmState();
			}
		}

		private static Mutex CreateNamedMutex()
		{
			try
			{
				return new Mutex(false, MUTEX_GLOBAL_NAME);
			}
			catch
			{
				return new Mutex(false, MUTEX_LOCAL_NAME);
			}
		}

		private static bool WithCrossProcessMutex(TimeSpan timeout, Action action)
		{
			using (var mutex = CreateNamedMutex())
			{
				try
				{
					if (!mutex.WaitOne(timeout))
						return false;

					try
					{
						action();
						return true;
					}
					finally
					{
						try { mutex.ReleaseMutex(); } catch { }
					}
				}
				catch (AbandonedMutexException)
				{
					try
					{
						action();
						return true;
					}
					finally
					{
						try { mutex.ReleaseMutex(); } catch { }
					}
				}
				catch
				{
					return false;
				}
			}
		}

		public void Save()
		{
			if (string.IsNullOrEmpty(_filePath))
				return;

			WithCrossProcessMutex(TimeSpan.FromSeconds(5), () =>
			{
				SaveNoMutex();
			});
		}

		private void SaveNoMutex()
		{
			lock (_globalLock)
			{
				LastUpdated = DateTime.UtcNow;
				CleanupExpiredEntries();

				string tempPath = _filePath + ".tmp";
				string backupPath = _filePath + ".bak";

				try
				{
					string json = JsonConvert.SerializeObject(this, Formatting.Indented);
					File.WriteAllText(tempPath, json);

					try
					{
						if (File.Exists(_filePath))
							File.Replace(tempPath, _filePath, backupPath, ignoreMetadataErrors: true);
						else
							File.Move(tempPath, _filePath);
					}
					catch
					{
						if (File.Exists(_filePath))
							File.Delete(_filePath);

						File.Move(tempPath, _filePath);
					}
				}
				catch
				{
					if (File.Exists(tempPath))
					{
						try { File.Delete(tempPath); } catch { }
					}
				}
			}
		}

		public void Reload()
		{
			if (string.IsNullOrEmpty(_filePath))
				return;

			var fresh = LoadFromFile(_filePath);
			fresh._filePath = _filePath;

			Version = fresh.Version;
			LastUpdated = fresh.LastUpdated;
			Instances = fresh.Instances;
			ScannedTargets = fresh.ScannedTargets;
			ClaimedAttacks = fresh.ClaimedAttacks;
		}

		public void UpdateInstanceHeartbeat(string instanceName)
		{
			var instance = Instances.FirstOrDefault(i => i.Name == instanceName);
			if (instance == null)
			{
				instance = new FarmBotInstance
				{
					Name = instanceName,
					IsActive = true
				};
				Instances.Add(instance);
			}

			instance.LastHeartbeat = DateTime.UtcNow;
			instance.IsActive = true;
		}

		public List<FarmBotInstance> GetActiveBots()
		{
			return Instances.Where(i => i.IsAlive(TimeSpan.FromMinutes(5))).ToList();
		}

		public ScannedTarget GetRecentScan(Coordinate coord, int withinHours = 8)
		{
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			return ScannedTargets.FirstOrDefault(s =>
				s.Coord == coordStr &&
				(DateTime.UtcNow - s.ScannedAt).TotalHours < withinHours);
		}

		public void AddScannedTarget(Coordinate coord, string scannedBy, long totalResources,
			bool hasFleet, bool hasDefense, int expiresInHours = 8)
		{
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";

			ScannedTargets.RemoveAll(s => s.Coord == coordStr);

			ScannedTargets.Add(new ScannedTarget
			{
				Coord = coordStr,
				ScannedBy = scannedBy,
				ScannedAt = DateTime.UtcNow,
				ExpiresAt = DateTime.UtcNow.AddHours(expiresInHours),
				TotalResources = totalResources,
				HasFleet = hasFleet,
				HasDefense = hasDefense
			});
		}

		public void UpdateScannedTarget(Coordinate coord, long totalResources, bool hasFleet, bool hasDefense)
		{
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			var existing = ScannedTargets.FirstOrDefault(s => s.Coord == coordStr);

			if (existing != null)
			{
				existing.TotalResources = totalResources;
				existing.HasFleet = hasFleet;
				existing.HasDefense = hasDefense;
			}
		}

		public List<ScannedTarget> GetGoodTargets(long minResources, int maxAgeMinutes = 30)
		{
			return ScannedTargets
				.Where(s => !s.IsExpired())
				.Where(s => (DateTime.UtcNow - s.ScannedAt).TotalMinutes <= maxAgeMinutes)
				.Where(s => s.TotalResources > 0)
				.Where(s => s.TotalResources >= minResources)
				.Where(s => !s.HasFleet)
				.Where(s => !s.HasDefense)
				.Where(s => !IsTargetClaimed(s.GetCoordinate()))
				.OrderByDescending(s => s.TotalResources)
				.ToList();
		}

		public List<ScannedTarget> GetTargetsWithoutReports(string scannedByInstance = null)
		{
			var query = ScannedTargets
				.Where(s => !s.IsExpired())
				.Where(s => s.TotalResources == 0);

			if (!string.IsNullOrEmpty(scannedByInstance))
				query = query.Where(s => s.ScannedBy == scannedByInstance);

			return query.OrderBy(s => s.ScannedAt).ToList();
		}

		public bool ShouldRescan(Coordinate coord, int maxAgeMinutes = 30)
		{
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			var scan = ScannedTargets.FirstOrDefault(s => s.Coord == coordStr && !s.IsExpired());

			if (scan == null) return true;
			if (scan.TotalResources == 0) return true;

			return (DateTime.UtcNow - scan.ScannedAt).TotalMinutes > maxAgeMinutes;
		}

		public bool TryClaimAttack(Coordinate coord, string claimedBy, DateTime returnsAt)
		{
			Reload();
			return TryClaimAttack_NoReload(coord, claimedBy, returnsAt);
		}

		private bool TryClaimAttack_NoReload(Coordinate coord, string claimedBy, DateTime returnsAt)
		{
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";

			var existingClaim = ClaimedAttacks.FirstOrDefault(c => c.Coord == coordStr && !c.IsExpired());
			if (existingClaim != null && existingClaim.ClaimedBy != claimedBy)
				return false;

			ClaimedAttacks.RemoveAll(c => c.Coord == coordStr);
			ClaimedAttacks.Add(new ClaimedAttack
			{
				Coord = coordStr,
				ClaimedBy = claimedBy,
				ClaimedAt = DateTime.UtcNow,
				AttackSentAt = null,
				ReturnsAt = returnsAt
			});

			return true;
		}

		public void MarkAttackSent(Coordinate coord, string claimedBy)
		{
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			var claim = ClaimedAttacks.FirstOrDefault(c => c.Coord == coordStr && c.ClaimedBy == claimedBy);
			if (claim != null)
			{
				claim.AttackSentAt = DateTime.UtcNow;
			}
		}

		public bool IsTargetClaimed(Coordinate coord)
		{
			if (coord == null) return false;
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			return ClaimedAttacks.Any(c => c.Coord == coordStr && !c.IsExpired());
		}

		public string GetClaimOwner(Coordinate coord)
		{
			if (coord == null) return null;
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			return ClaimedAttacks.FirstOrDefault(c => c.Coord == coordStr && !c.IsExpired())?.ClaimedBy;
		}

		private void CleanupExpiredEntries()
		{
			ScannedTargets.RemoveAll(s => s.IsExpired());
			ClaimedAttacks.RemoveAll(c => c.IsExpired());

			foreach (var instance in Instances)
			{
				if (!instance.IsAlive(TimeSpan.FromMinutes(5)))
					instance.IsActive = false;
			}
		}

		public string GetStats()
		{
			CleanupExpiredEntries();
			return $"Instances: {Instances.Count(i => i.IsActive)} active, " +
			       $"Scanned: {ScannedTargets.Count}, " +
			       $"Claims: {ClaimedAttacks.Count}";
		}

		public static void AddScannedTargetAtomic(string filePath, Coordinate coord, string scannedBy,
			long totalResources, bool hasFleet, bool hasDefense)
		{
			WithCrossProcessMutex(TimeSpan.FromSeconds(5), () =>
			{
				var state = LoadFromFile(filePath);
				state._filePath = filePath;

				state.AddScannedTarget(coord, scannedBy, totalResources, hasFleet, hasDefense);
				state.SaveNoMutex();
			});
		}

		public static void UpdateScannedTargetAtomic(string filePath, Coordinate coord, long totalResources, bool hasFleet, bool hasDefense)
		{
			WithCrossProcessMutex(TimeSpan.FromSeconds(5), () =>
			{
				var state = LoadFromFile(filePath);
				state._filePath = filePath;

				state.UpdateScannedTarget(coord, totalResources, hasFleet, hasDefense);
				state.SaveNoMutex();
			});
		}

		public static void MarkAttackSentAtomic(string filePath, Coordinate coord, string claimedBy)
		{
			WithCrossProcessMutex(TimeSpan.FromSeconds(5), () =>
			{
				var state = LoadFromFile(filePath);
				state._filePath = filePath;

				state.MarkAttackSent(coord, claimedBy);
				state.SaveNoMutex();
			});
		}

		public static bool TryReserveScan(string filePath, Coordinate coord, string scannedBy,
			int withinHours = 8, int maxAgeMinutes = 30)
		{
			bool result = false;

			bool acquired = WithCrossProcessMutex(TimeSpan.FromSeconds(5), () =>
			{
				var state = LoadFromFile(filePath);
				state._filePath = filePath;

				if (state.ShouldRescan(coord, maxAgeMinutes))
				{
					state.AddScannedTarget(coord, scannedBy, 0, false, false);
					state.SaveNoMutex();
					result = true;
					return;
				}

				string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
				var recentScan = state.ScannedTargets.FirstOrDefault(s =>
					s.Coord == coordStr &&
					(DateTime.UtcNow - s.ScannedAt).TotalHours < withinHours);

				if (recentScan != null)
				{
					if (recentScan.ScannedBy == scannedBy)
					{
						if (state.ShouldRescan(coord, maxAgeMinutes))
						{
							state.AddScannedTarget(coord, scannedBy, 0, false, false);
							state.SaveNoMutex();
							result = true;
							return;
						}
						result = false;
						return;
					}
					else
					{
						if ((DateTime.UtcNow - recentScan.ScannedAt).TotalMinutes <= maxAgeMinutes)
						{
							result = false;
							return;
						}

						state.AddScannedTarget(coord, scannedBy, 0, false, false);
						state.SaveNoMutex();
						result = true;
						return;
					}
				}

				state.AddScannedTarget(coord, scannedBy, 0, false, false);
				state.SaveNoMutex();
				result = true;
			});

			if (!acquired)
				return false;

			return result;
		}

		public static bool TryClaimAttackAtomic(string filePath, Coordinate coord, string claimedBy, DateTime returnsAt)
		{
			bool claimed = false;

			bool acquired = WithCrossProcessMutex(TimeSpan.FromSeconds(5), () =>
			{
				var state = LoadFromFile(filePath);
				state._filePath = filePath;

				claimed = state.TryClaimAttack_NoReload(coord, claimedBy, returnsAt);

				if (claimed)
					state.SaveNoMutex();
			});

			if (!acquired)
				return false;

			return claimed;
		}
	}
}
