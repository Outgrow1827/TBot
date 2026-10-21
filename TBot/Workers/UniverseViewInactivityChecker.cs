using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TBot.Common.Logging;
using Microsoft.Extensions.Logging;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Models;
using Tbot.Common.Settings;
using Tbot.Services;

namespace Tbot.Workers {
	/// <summary>Inactivity detection from galaxy scans (universe view) instead of /api/players.xml, whose status flags can lag reality: a player counts as inactive when their planet carries the Inactive flag in the scan.</summary>
	public class UniverseViewInactivityChecker {
		private readonly IOgameService _ogameService;
		private readonly FarmTargetCache _farmTargetCache;
		private readonly ITBotMain _tbotInstance;

		// Cache of recent galaxy scans to avoid re-scanning the same system
		private readonly Dictionary<string, DateTime> _recentScans = new();
		private readonly TimeSpan _scanCacheTtl = TimeSpan.FromMinutes(30);

		public UniverseViewInactivityChecker(IOgameService ogameService, FarmTargetCache farmTargetCache, ITBotMain tbotInstance) {
			_ogameService = ogameService;
			_farmTargetCache = farmTargetCache;
			_tbotInstance = tbotInstance;
		}

		private int GetInactivityThresholdDays() {
			try {
				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "InactivityThresholdDays")) {
					return (int)_tbotInstance.InstanceSettings.AutoFarm.InactivityThresholdDays;
				}
			} catch {
			}
			return 7; // default 7 days
		}

		/// <summary>Checks inactivity from the universe view: true if inactive, false if active, null if unknown.</summary>
		/// <param name="coordinate">The coordinate to check</param> <param name="playerId">Optional player ID for cross-reference</param>
		public async Task<bool?> IsInactiveViaUniverseView(Coordinate coordinate, int? playerId = null) {
			try {
				// First check the persistent cache (FastFarm database) - this is the "eternal" record
				var cachedEntry = _farmTargetCache?.Get(coordinate);
				if (cachedEntry != null && cachedEntry.IsInactive && cachedEntry.InactiveStatusLocked) {
					// Inactive status is locked/eternal in the database - trust it
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, 
						$"UniverseView: {coordinate} inactive status LOCKED in DB (source: {cachedEntry.InactivitySource}, since: {cachedEntry.InactiveSince})");
					return true;
				}

				// NEW: Check player-level activity if playerId provided
				if (playerId.HasValue && playerId.Value > 0 && _farmTargetCache != null) {
					if (_farmTargetCache.IsPlayerActive(playerId.Value, out DateTime lastChecked, out int? activityDays)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"UniverseView: {coordinate} player {playerId.Value} is ACTIVE (last checked: {lastChecked:u}, activity: {activityDays}d). Skipping coordinate.");
						return false;
					}
				}

				// Check if we have a recent galaxy scan for this system
				var scanKey = $"{coordinate.Galaxy}:{coordinate.System}";
				if (_recentScans.TryGetValue(scanKey, out var scanTime) && 
					DateTime.UtcNow - scanTime < _scanCacheTtl) {
					// Use cached scan data from memory (will be checked below)
				} else {
					// Perform a fresh galaxy scan for this system
					await RefreshSystemScan(coordinate.Galaxy, coordinate.System);
				}

				// Get the latest scan data from cache
				var entries = _farmTargetCache?.GetInRange(coordinate.Galaxy, coordinate.System, coordinate.System);
				var entry = entries?.FirstOrDefault(e => e.Coordinate.IsSame(coordinate));
				
				if (entry != null) {
					if (entry.IsInactive && entry.InactiveStatusLocked) {
						// Confirmed inactive in universe view with eternal persistence
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"UniverseView: {coordinate} confirmed INACTIVE via galaxy scan (source: {entry.InactivitySource})");
						return true;
					} else if (!entry.IsInactive) {
						// Explicitly NOT inactive in latest scan
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"UniverseView: {coordinate} is ACTIVE per latest galaxy scan");
						return false;
					}
				}

				// If we have a player ID, we could cross-reference but universe view is authoritative
				// If coordinate not found in scan, we can't determine - return null (unknown)
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"UniverseView: {coordinate} status UNKNOWN (not in recent scan)");
				return null;
			} catch (Exception ex) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"UniverseView: Error checking inactivity for {coordinate}: {ex.Message}");
				return null;
			}
		}

		/// <summary>Pre-attack check that always issues a fresh galaxy scan, never trusting the eternal InactiveStatusLocked shortcut or the recent-scan cache.</summary>
		/// <returns>True if still inactive, false if the player is active now, null if the scan failed; the caller aborts on anything but true.</returns>
		public async Task<bool?> IsStillInactiveLive(Coordinate coordinate, int? playerId = null) {
			try {
				var galaxyInfo = await _ogameService.GetGalaxyInfo(coordinate.Galaxy, coordinate.System);
				var planet = galaxyInfo?.Planets?.FirstOrDefault(p => p != null && p.HasCoords(coordinate));
				if (planet == null) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
						$"Live pre-attack recheck: {coordinate} not found in fresh galaxy scan - treating as unknown.");
					return null;
				}

				// planet.Inactive is the game's own classification; the raw activity/showMinutes number is ambiguous at the boundaries.
				bool stillInactive = planet.Inactive;
				int? activityDays = planet.Activity > 0 ? (planet.Activity + 1439) / 1440 : (int?) null;

				// Reflect the fresh result back into persistent state so later eternal-lock checks (and
				// other AutoFarm/FastFarm cycles) benefit too, instead of only fixing this one dispatch.
				var entry = _farmTargetCache?.Get(coordinate);
				if (entry != null) {
					entry.LastSeenDate = DateTime.UtcNow;
					if (activityDays.HasValue) entry.LastActivityDays = activityDays.Value;
					if (!stillInactive) {
						entry.IsInactive = false;
						entry.InactiveStatusLocked = false;
						entry.InactivitySource = "universe_view_live_recheck";
					}
					await _farmTargetCache.Upsert(entry);
				}
				if (playerId.HasValue && playerId.Value > 0 && !string.IsNullOrEmpty(planet.Player?.Name)) {
					_farmTargetCache?.SetPlayerActivity(playerId.Value, planet.Player.Name, !stillInactive, activityDays);
				}

				_tbotInstance.log(stillInactive ? LogLevel.Debug : LogLevel.Warning, LogSender.AutoFarm,
					$"Live pre-attack recheck: {coordinate} is {(stillInactive ? "still INACTIVE" : "ACTIVE")} per fresh galaxy scan.");
				return stillInactive;
			} catch (Exception ex) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"Live pre-attack recheck failed for {coordinate}: {ex.Message}");
				return null;
			}
		}

		/// <summary>Scans a system and stores the universe view data in the FarmTargetCache, which is how inactivity is detected and persisted.</summary>
		public async Task RefreshSystemScan(int galaxy, int system) {
			try {
				var galaxyInfo = await _ogameService.GetGalaxyInfo(galaxy, system);
				if (galaxyInfo?.Planets != null) {
					foreach (var planet in galaxyInfo.Planets.Where(p => p != null)) {
						var entry = _farmTargetCache.Get(planet.Coordinate) 
							?? new FarmTargetCacheEntry { Coordinate = planet.Coordinate };

						// Update with universe view data
						entry.PlayerId = planet.Player?.ID ?? entry.PlayerId;
						entry.PlayerName = planet.Player?.Name ?? entry.PlayerName;
						entry.PlayerRank = planet.Player?.Rank ?? entry.PlayerRank;
						entry.IsAdministrator = planet.Administrator;
						entry.IsBanned = planet.Banned;
						entry.IsVacation = planet.Vacation;
						entry.LastSeenDate = DateTime.UtcNow;

						// planet.Inactive is the authoritative signal; planet.Activity (showMinutes) is ambiguous (0 means no badge shown, not abandoned), so it only refines how long a game-confirmed inactive planet has been idle and never promotes an active planet to inactive.
						int? activityDays = planet.Activity > 0 ? (planet.Activity + 1439) / 1440 : (int?) null;
						if (activityDays.HasValue) entry.LastActivityDays = activityDays.Value;

						if (planet.Inactive) {
							if (!entry.IsInactive || !entry.InactiveStatusLocked) {
								entry.IsInactive = true;
								entry.InactivitySource = "universe_view";
								entry.InactiveSince ??= DateTime.UtcNow;
								entry.InactiveStatusLocked = true; // ETERNAL persistence
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									$"UniverseView: {planet.Coordinate} marked INACTIVE (eternal) via galaxy scan (game-confirmed{(activityDays.HasValue ? $", activity {activityDays}d" : "")})");
							}
							if (entry.PlayerId > 0 && !string.IsNullOrEmpty(entry.PlayerName)) {
								_farmTargetCache.SetPlayerActivity(entry.PlayerId, entry.PlayerName, false, activityDays);
							}
						} else {
							// The game shows this player as active, so the player is marked active and the coordinate is corrected even if it was locked before.
							if (entry.PlayerId > 0 && !string.IsNullOrEmpty(entry.PlayerName)) {
								_farmTargetCache.SetPlayerActivity(entry.PlayerId, entry.PlayerName, true, activityDays);
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									$"UniverseView: PLAYER {entry.PlayerName} (ID:{entry.PlayerId}) marked ACTIVE - {planet.Coordinate} is active per galaxy scan. All coordinates for this player will be skipped.");
							}
							entry.IsInactive = false;
							entry.InactiveStatusLocked = false;
							entry.InactivitySource = "universe_view";
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
								$"UniverseView: {planet.Coordinate} marked ACTIVE via galaxy scan (game-confirmed)");
						}

						await _farmTargetCache.Upsert(entry);
					}
				}

				// Mark this system as recently scanned
				_recentScans[$"{galaxy}:{system}"] = DateTime.UtcNow;
				_farmTargetCache?.MarkSystemScanned(galaxy, system);
			} catch (Exception ex) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"UniverseView: Error refreshing system scan {galaxy}:{system}: {ex.Message}");
			}
		}

		/// <summary>Marks a target's inactive status as confirmed by an espionage report, reinforcing the eternal lock.</summary>
		public async Task ConfirmInactivityFromEspionage(Coordinate coordinate) {
			var entry = _farmTargetCache.Get(coordinate);
			if (entry != null && entry.IsInactive) {
				entry.InactivitySource = "espionage_report";
				entry.InactiveStatusLocked = true;
				await _farmTargetCache.Upsert(entry);
			}
		}

		/// <summary>Gets the known inactive targets in a galaxy range from the persistent cache, for FastFarmMode attacks without live scans.</summary>
		public List<FarmTargetCacheEntry> GetInactiveTargetsInRange(int galaxy, int startSystem, int endSystem) {
			var candidates = _farmTargetCache?.GetInRange(galaxy, startSystem, endSystem)
				.Where(e => e.IsInactive && e.InactiveStatusLocked && !e.IsAdministrator && !e.IsBanned && !e.IsVacation)
				.ToList() ?? new List<FarmTargetCacheEntry>();

			// Filter out targets whose player is marked as active
			var activePlayerIds = _farmTargetCache?.GetActivePlayerIds() ?? new List<int>();
			if (activePlayerIds.Count > 0) {
				candidates = candidates.Where(e => !activePlayerIds.Contains(e.PlayerId)).ToList();
			}

			return candidates;
		}
	}
}