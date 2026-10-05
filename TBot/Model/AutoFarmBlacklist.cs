using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public enum BlacklistReason {
		HasFleet,
		HasDefense,
		LowResources,
		ManuallyAdded,
		ProbeFailure
	}

	public class BlacklistedTarget {
		public Coordinate Coordinate { get; set; }
		public BlacklistReason Reason { get; set; }
		public DateTime BlacklistedAt { get; set; }
		public DateTime ExpiresAt { get; set; }

		public BlacklistedTarget() {
		}

		public BlacklistedTarget(Coordinate coordinate, BlacklistReason reason, DateTime expiresAt) {
			Coordinate = coordinate;
			Reason = reason;
			BlacklistedAt = DateTime.UtcNow;
			ExpiresAt = expiresAt;
		}

		public bool IsExpired() {
			return DateTime.UtcNow >= ExpiresAt;
		}
	}

	public class BlacklistedPlayer {
		public string PlayerName { get; set; }
		public BlacklistReason Reason { get; set; }
		public DateTime BlacklistedAt { get; set; }
		public DateTime ExpiresAt { get; set; }

		public BlacklistedPlayer() {
		}

		public BlacklistedPlayer(string playerName, BlacklistReason reason, DateTime expiresAt) {
			PlayerName = playerName;
			Reason = reason;
			BlacklistedAt = DateTime.UtcNow;
			ExpiresAt = expiresAt;
		}

		public bool IsExpired() {
			return DateTime.UtcNow >= ExpiresAt;
		}
	}

	public class AutoFarmBlacklist {
		private List<BlacklistedTarget> _blacklistedTargets;
		private List<BlacklistedPlayer> _blacklistedPlayers;
		private readonly object _lock = new object();
		private string _filePath;

		public AutoFarmBlacklist() {
			_blacklistedTargets = new List<BlacklistedTarget>();
			_blacklistedPlayers = new List<BlacklistedPlayer>();
		}

		public AutoFarmBlacklist(string filePath) {
			string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			if (!Directory.Exists(dataFolder)) {
				Directory.CreateDirectory(dataFolder);
			}
			_filePath = Path.Combine(dataFolder, filePath);
			_blacklistedTargets = new List<BlacklistedTarget>();
			_blacklistedPlayers = new List<BlacklistedPlayer>();
			LoadFromFile();
		}

		public void AddTarget(Coordinate coordinate, BlacklistReason reason, int hoursUntilReset) {
			lock (_lock) {
				_blacklistedTargets.RemoveAll(t => t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);

				DateTime expiresAt = DateTime.UtcNow.AddHours(hoursUntilReset);
				_blacklistedTargets.Add(new BlacklistedTarget(coordinate, reason, expiresAt));
				SaveToFile();
			}
		}

		public void AddPlayer(string playerName, BlacklistReason reason, int hoursUntilReset) {
			if (string.IsNullOrEmpty(playerName)) return;
			lock (_lock) {
				_blacklistedPlayers.RemoveAll(p => string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

				DateTime expiresAt = DateTime.UtcNow.AddHours(hoursUntilReset);
				_blacklistedPlayers.Add(new BlacklistedPlayer(playerName, reason, expiresAt));
				SaveToFile();
			}
		}

		public bool IsBlacklisted(Coordinate coordinate) {
			lock (_lock) {
				CleanupExpiredTargets();

				return _blacklistedTargets.Any(t =>
					t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);
			}
		}

		public bool IsPlayerBlacklisted(string playerName) {
			if (string.IsNullOrEmpty(playerName)) return false;
			lock (_lock) {
				CleanupExpiredTargets();

				return _blacklistedPlayers.Any(p =>
					string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
			}
		}

		public BlacklistedTarget GetBlacklistedTarget(Coordinate coordinate) {
			lock (_lock) {
				CleanupExpiredTargets();

				return _blacklistedTargets.FirstOrDefault(t =>
					t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);
			}
		}

		public BlacklistedPlayer GetBlacklistedPlayer(string playerName) {
			if (string.IsNullOrEmpty(playerName)) return null;
			lock (_lock) {
				CleanupExpiredTargets();

				return _blacklistedPlayers.FirstOrDefault(p =>
					string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
			}
		}

		public void RemoveTarget(Coordinate coordinate) {
			lock (_lock) {
				_blacklistedTargets.RemoveAll(t =>
					t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);
			}
			SaveToFile();
		}

		public void RemovePlayer(string playerName) {
			if (string.IsNullOrEmpty(playerName)) return;
			lock (_lock) {
				_blacklistedPlayers.RemoveAll(p =>
					string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
			}
			SaveToFile();
		}

		public void ClearAll() {
			lock (_lock) {
				_blacklistedTargets.Clear();
				_blacklistedPlayers.Clear();
			}
			SaveToFile();
		}

		public int GetBlacklistedCount() {
			lock (_lock) {
				CleanupExpiredTargets();
				return _blacklistedTargets.Count + _blacklistedPlayers.Count;
			}
		}

		public List<BlacklistedTarget> GetAllBlacklisted() {
			lock (_lock) {
				CleanupExpiredTargets();
				return new List<BlacklistedTarget>(_blacklistedTargets);
			}
		}

		public List<BlacklistedPlayer> GetAllBlacklistedPlayers() {
			lock (_lock) {
				CleanupExpiredTargets();
				return new List<BlacklistedPlayer>(_blacklistedPlayers);
			}
		}

		private void CleanupExpiredTargets() {
			_blacklistedTargets.RemoveAll(t => t.IsExpired());
			_blacklistedPlayers.RemoveAll(p => p.IsExpired());
		}

		public void ManualCleanup() {
			lock (_lock) {
				CleanupExpiredTargets();
			}
		}
		private void LoadFromFile() {
			if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) {
				return;
			}

			try {
				string json = File.ReadAllText(_filePath);
				var loaded = JsonConvert.DeserializeObject<BlacklistData>(json);
				if (loaded != null) {
					lock (_lock) {
						_blacklistedTargets = loaded.Targets ?? new List<BlacklistedTarget>();
						_blacklistedPlayers = loaded.Players ?? new List<BlacklistedPlayer>();
						CleanupExpiredTargets();
					}
				}
			} catch (Exception) {
				lock (_lock) {
					_blacklistedTargets = new List<BlacklistedTarget>();
					_blacklistedPlayers = new List<BlacklistedPlayer>();
				}
			}
		}

		private void SaveToFile() {
			if (string.IsNullOrEmpty(_filePath)) {
				return;
			}

			List<BlacklistedTarget> targetsSnapshot;
			List<BlacklistedPlayer> playersSnapshot;
			lock (_lock) {
				targetsSnapshot = _blacklistedTargets.ToList();
				playersSnapshot = _blacklistedPlayers.ToList();
			}

			try {
				var data = new BlacklistData {
					Targets = targetsSnapshot,
					Players = playersSnapshot
				};
				string json = JsonConvert.SerializeObject(data, Formatting.Indented);
				File.WriteAllText(_filePath, json);
			} catch (Exception) {
			}
		}

		private class BlacklistData {
			public List<BlacklistedTarget> Targets { get; set; }
			public List<BlacklistedPlayer> Players { get; set; }
		}
	}
}
