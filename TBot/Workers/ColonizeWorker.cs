using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Common.Logging;
using Tbot.Common.Settings;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using Tbot.Services;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using TBot.Model;
using TBot.Ogame.Infrastructure.Models;
using TBot.Ogame.Infrastructure;

namespace Tbot.Workers {
	public class ColonizeWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;

		// Scan-once-remember-after cache for galaxy occupancy lookups, static so it survives worker re-instantiations and evicted on every write so it cannot grow for the process lifetime.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (GalaxyInfo Info, DateTime FetchedAt)> _galaxyScanCache = new();
		// Hard cap independent of CheckIntervalMax, in case a very wide scan range (many
		// galaxies) is configured - bounds worst-case memory instead of growing unbounded.
		private const int MaxGalaxyScanCacheEntries = 1000;

		private PlayerStatusCache _playerStatusCache;

		public ColonizeWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_ogameService = ogameService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		// Retries with backoff on transient errors instead of aborting the whole check on one bad request.
		private async Task<GalaxyInfo> GetGalaxyInfoWithRetry(Coordinate coordinate) {
			int retryCount = 0;
			int maxRetries = 5;
			while (true) {
				try {
					return await _ogameService.GetGalaxyInfo(coordinate);
				} catch (Exception e) when (e.Message.Contains("system must be within") || e.Message.Contains("503") || e.Message.Contains("Service Unavailable")) {
					retryCount++;
					if (retryCount >= maxRetries) {
						DoLog(LogLevel.Warning, $"Galaxy scan for {coordinate} kept failing after {maxRetries} retries: {e.Message}. Giving up on this system.");
						throw;
					}
					int waitSeconds = retryCount * 3;
					DoLog(LogLevel.Warning, $"Galaxy scan failed for {coordinate}. Retry {retryCount}/{maxRetries} in {waitSeconds}s...");
					await Task.Delay(waitSeconds * 1000);
				}
			}
		}

		// Cached wrapper: the network is only hit the first time a system is seen or once the cached result goes stale, with freshness tied to CheckIntervalMax.
		private async Task<GalaxyInfo> GetGalaxyInfoCached(Coordinate coordinate) {
			// The cache is process-wide, so the key includes the universe: otherwise two instances on different servers would read each other's occupancy data.
			string key = $"{_tbotInstance.InstanceSettings.Credentials.Universe}:{coordinate.Galaxy}:{coordinate.System}";
			TimeSpan maxAge = TimeSpan.FromMinutes((int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMax);
			if (_galaxyScanCache.TryGetValue(key, out var cached)) {
				if (DateTime.UtcNow - cached.FetchedAt < maxAge) {
					return cached.Info;
				}
				// Stale entries are removed instead of being silently overwritten, so a key that stops being scanned doesn't linger forever.
				_galaxyScanCache.TryRemove(key, out _);
			}
			GalaxyInfo info = await GetGalaxyInfoWithRetry(coordinate);
			await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds));
			if (_galaxyScanCache.Count >= MaxGalaxyScanCacheEntries) {
				// Hard cap hit (very wide scan range) - drop the oldest entries to make room
				// instead of growing past the cap.
				foreach (var oldKey in _galaxyScanCache.OrderBy(kv => kv.Value.FetchedAt).Take(_galaxyScanCache.Count - MaxGalaxyScanCacheEntries + 1).Select(kv => kv.Key).ToList()) {
					_galaxyScanCache.TryRemove(oldKey, out _);
				}
			}
			_galaxyScanCache[key] = (info, DateTime.UtcNow);
			return info;
		}

		// Each AutoColonize.Exclude entry is either a single coordinate or a range; an empty list excludes nothing.
		private static bool HasKey(dynamic entry, string key) {
			foreach (var value in (IEnumerable<string>) entry.Keys)
				if (value == key)
					return true;
			return false;
		}

		private bool ShouldExcludeSystem(int galaxy, int system) {
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoColonize, "Exclude"))
				return false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoColonize.Exclude) {
				if ((int) exclude.Galaxy != galaxy)
					continue;

				bool isRange = HasKey(exclude, "StartSystem") && HasKey(exclude, "EndSystem");
				if (isRange) {
					if (HasKey(exclude, "StartPosition") || HasKey(exclude, "EndPosition"))
						continue; // a position range only excludes specific planets, not the whole system
					if (system >= (int) exclude.StartSystem && system <= (int) exclude.EndSystem) {
						DoLog(LogLevel.Information, $"Skipping system {system}: system in exclude range {(int) exclude.StartSystem}-{(int) exclude.EndSystem}.");
						return true;
					}
				} else if (!HasKey(exclude, "Position") && (int) exclude.System == system) {
					DoLog(LogLevel.Information, $"Skipping system {system}: system in exclude list.");
					return true;
				}
			}
			return false;
		}

		private bool ShouldExcludeTarget(Coordinate coord) {
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoColonize, "Exclude"))
				return false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoColonize.Exclude) {
				if ((int) exclude.Galaxy != coord.Galaxy)
					continue;

				bool isRange = HasKey(exclude, "StartSystem") && HasKey(exclude, "EndSystem");
				if (isRange) {
					if (coord.System < (int) exclude.StartSystem || coord.System > (int) exclude.EndSystem)
						continue;
					bool hasPositionRange = HasKey(exclude, "StartPosition") && HasKey(exclude, "EndPosition");
					if (hasPositionRange) {
						if (coord.Position >= (int) exclude.StartPosition && coord.Position <= (int) exclude.EndPosition) {
							DoLog(LogLevel.Information, $"Skipping {coord}: coordinate in exclude range.");
							return true;
						}
					} else {
						DoLog(LogLevel.Information, $"Skipping {coord}: coordinate in exclude range (whole system range, no position bounds).");
						return true;
					}
				} else if (HasKey(exclude, "Position") && (int) exclude.System == coord.System && (int) exclude.Position == coord.Position) {
					DoLog(LogLevel.Information, $"Skipping {coord}: coordinate in exclude list.");
					return true;
				}
			}
			return false;
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool) _tbotInstance.InstanceSettings.AutoColonize.Active;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "Colonize";
		}
		public override Feature GetFeature() {
			return Feature.Colonize;
		}

		public override LogSender GetLogSender() {
			return LogSender.Colonize;
		}

		protected override async Task Execute() {
			await _tbotOgameBridge.CheckCelestials();
			bool stop = false;
			Fields fieldsSettings = new() {
				Total = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MinFields
			};
			Temperature temperaturesSettings = new() {
				Min = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MinTemperatureAcceptable,
				Max = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MaxTemperatureAcceptable
			};
			try {
				if ((bool) _tbotInstance.InstanceSettings.AutoColonize.Abandon.Active) {
					DoLog(LogLevel.Information, "Detecting planet to abandon");

					List<Celestial> newCelestials = _tbotInstance.UserData.celestials.ToList();
					var dic = new Dictionary<Coordinate, Celestial>();

					int homePlanetId = _calculationService.GetHomePlanetId(_tbotInstance.UserData.celestials);

					foreach (Planet planet in _tbotInstance.UserData.celestials.Where(c => c is Planet)) {
						if (planet.ID == homePlanetId) {
							DoLog(LogLevel.Debug, $"Skipping abandon check on {planet.ToString()}: this is the main/home planet (lowest planet ID), never abandon it.");
							continue;
						}
						Planet tempCelestial = await _tbotOgameBridge.UpdatePlanet(planet, UpdateTypes.Fast) as Planet;
						if (tempCelestial.Coordinate.Type == Celestials.Planet && tempCelestial.Fields.Built == 0) {
							string abandonCriteria = $"fields {tempCelestial.Fields.Total}/{fieldsSettings.Total} (min required), temperature {tempCelestial.Temperature.Max}°C (acceptable range {temperaturesSettings.Min}°C to {temperaturesSettings.Max}°C)";
							if (_calculationService.ShouldAbandon(tempCelestial as Planet, tempCelestial.Fields.Total, tempCelestial.Temperature.Max, fieldsSettings, temperaturesSettings)) {
								DoLog(LogLevel.Debug, $"This planet should be abandoned: {tempCelestial.ToString()} - {abandonCriteria}");
								if (await _ogameService.AbandonCelestial(tempCelestial)) {
									DoLog(LogLevel.Debug, $"Successful Abandon on {tempCelestial.ToString()} - {abandonCriteria}.");
									// The freed system may still be cached as occupied, so it is dropped and re-scanned live next time.
									_galaxyScanCache.TryRemove($"{_tbotInstance.InstanceSettings.Credentials.Universe}:{tempCelestial.Coordinate.Galaxy}:{tempCelestial.Coordinate.System}", out _);
								} else {
									DoLog(LogLevel.Debug, $"Failed Abandon on {tempCelestial.ToString()} - {abandonCriteria}.");
								}
							} else {
								DoLog(LogLevel.Debug, $"No planet should be abandoned - {tempCelestial.ToString()}: {abandonCriteria}");
							}
						}
					}
					await _tbotOgameBridge.CheckCelestials();
					DoLog(LogLevel.Information, "End of planet abandonment");
				}

				if ((bool) _tbotInstance.InstanceSettings.AutoColonize.Active) {
					long interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMax);
					_tbotInstance.log(LogLevel.Information, LogSender.Colonize, "Checking if a new planet is needed...");

					_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
					var maxPlanets = _calculationService.CalcMaxPlanets(_tbotInstance.UserData.researches);
					var currentPlanets = _tbotInstance.UserData.celestials.Where(c => c.Coordinate.Type == Celestials.Planet).Count();
					var slotsToLeaveFree = (int) (_tbotInstance.InstanceSettings.AutoColonize.SlotsToLeaveFree ?? 0);
					if (currentPlanets + slotsToLeaveFree < maxPlanets) {
						_tbotInstance.log(LogLevel.Information, LogSender.Colonize, "A new planet is needed.");

						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						List<RankSlotsPriority> rankSlotsPriority = new() {
							new RankSlotsPriority(Feature.BrainAutoMine,
								(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
								((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.Transports.Active && ((bool) _tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.AutoResearch.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
								(int) _tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
								(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
							new RankSlotsPriority(Feature.Expeditions,
								(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
								(bool) _tbotInstance.InstanceSettings.Expeditions.Active,
								(int) _tbotInstance.UserData.slots.ExpTotal,
								(int)_tbotInstance.UserData.slots.ExpInUse),
							new RankSlotsPriority(Feature.AutoFarm,
								(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Active,
								(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
								(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)),
							new RankSlotsPriority(Feature.Colonize,
								(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize,
								(bool) _tbotInstance.InstanceSettings.AutoColonize.Active,
								(bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
									(int) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots :
									1,
								(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
							new RankSlotsPriority(Feature.AutoDiscovery,
								(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
								(bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active,
								(int) _tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
								(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
							new RankSlotsPriority(Feature.Harvest,
								(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
								(bool) _tbotInstance.InstanceSettings.AutoHarvest.Active,
								(int) _tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
								(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
						};
						int MaxSlots = _calculationService.CalcSlotsPriority(Feature.Colonize, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);

						if (
							(!(bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active && _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize && !f.ReturnFlight) >= maxPlanets - currentPlanets)
							|| ((bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active && _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize && !f.ReturnFlight) > 0)
						) {
							_tbotInstance.log(LogLevel.Information, LogSender.Colonize, "Colony Ship(s) already in flight.");
							interval = (_tbotInstance.UserData.fleets
								.OrderBy(f => f.ArriveIn)
							.First(f => !f.ReturnFlight)
								.ArriveIn * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds);
						} else {
							Coordinate originCoords = new(
								(int) _tbotInstance.InstanceSettings.AutoColonize.Origin.Galaxy,
								(int) _tbotInstance.InstanceSettings.AutoColonize.Origin.System,
								(int) _tbotInstance.InstanceSettings.AutoColonize.Origin.Position,
								Enum.Parse<Celestials>((string) _tbotInstance.InstanceSettings.AutoColonize.Origin.Type)
							);
							Celestial origin = _tbotInstance.UserData.celestials.Single(c => c.HasCoords(originCoords));
							origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Ships);
							origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBonuses);

							var neededColonizers = maxPlanets - currentPlanets - slotsToLeaveFree;

							if (origin.Ships.ColonyShip >= neededColonizers) {
								var minTemp = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MinTemperatureAcceptable;
								var maxTemp = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MaxTemperatureAcceptable;
								var minFields = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MinFields;
								List <Coordinate> targets = new();
								// Candidates of all ranges are pooled and picked globally by distance, so the per-range quota is enforced again in the dispatch loop below.
								var targetRangeIndex = new Dictionary<Coordinate, int>();
								var rangeRemainingQuota = new Dictionary<int, int>();
								int rangeIdx = -1;
								foreach (var t in _tbotInstance.InstanceSettings.AutoColonize.Targets) {
									rangeIdx++;
									var planetsInThisRange = _calculationService.CountPlanetsInRange(_tbotInstance.UserData.celestials, (int) t.Galaxy, (int) t.StartSystem, (int) t.EndSystem, (int) t.StartPosition, (int) t.EndPosition, minFields, minTemp, maxTemp);
									var maxPlanetsInThisRange = (int) t.MaxPlanets;
									rangeRemainingQuota[rangeIdx] = Math.Max(0, maxPlanetsInThisRange - planetsInThisRange);
									if (planetsInThisRange >= maxPlanetsInThisRange) {
										DoLog(LogLevel.Information, $"You already have {planetsInThisRange.ToString()} planets that fit temperature and fields settings in the range [{t.Galaxy}:{t.StartSystem}-{t.EndSystem}:{t.StartPosition}-{t.EndPosition}]. The max number is {maxPlanetsInThisRange}. Skipping...");
										continue;
									}

									bool targetEmptySystems = SettingsService.IsSettingSet(t, "TargetEmptySystems") && (bool) t.TargetEmptySystems;
									int emptySystemsBuffer = SettingsService.IsSettingSet(t, "EmptySystemsBuffer") ? (int) t.EmptySystemsBuffer : 0;
									HashSet<int> excludeSystems = new();
									if (SettingsService.IsSettingSet(t, "ExcludeSystems")) {
										foreach (var excludedSystem in t.ExcludeSystems) {
											excludeSystems.Add((int) excludedSystem);
										}
									}

									int maxSystemNumber = (int) _tbotInstance.UserData.serverData.Systems;

									// With IgnoreInactivePlayers, only the "I" status (long inactive) stops blocking a system, which a galaxy scan can't tell apart, so the real status string is used and unknown counts as blocking.
									bool ignoreInactivePlayers = SettingsService.IsSettingSet(t, "IgnoreInactivePlayers") && (bool) t.IgnoreInactivePlayers;
									if (ignoreInactivePlayers)
										_playerStatusCache ??= new PlayerStatusCache(_ogameService);

									async Task<bool> IsBlockingPlanet(Planet p) {
										if (p == null || p.Banned)
											return false;
										if (!ignoreInactivePlayers)
											return true;
										string status = p.Player != null ? await _playerStatusCache.GetStatus(p.Player.ID) : null;
										if (status == null)
											return p.Inactive; // unknown status - fall back to the galaxy scan's own flag
										return !status.Contains('I');
									}

									// Only planets of other, non-banned players occupy a buffer system; the user's own planets nearby don't block colonization.
									async Task<bool> BufferSystemBlocked(GalaxyInfo bufferSystem) {
										foreach (var p in bufferSystem.Planets) {
											if (p != null && p.Player != null && p.Player.ID != _tbotInstance.UserData.userInfo.PlayerID && await IsBlockingPlanet(p))
												return true;
										}
										return false;
									}

									// The buffer check slides per candidate, so empty pockets anywhere inside a wide range are found instead of the whole range being discarded.
									async Task<(bool Ok, int BlockedAt)> HasEmptyBuffer(int system) {
										if (emptySystemsBuffer <= 0) {
											return (true, 0);
										}
										for (int b = Math.Max(1, system - emptySystemsBuffer); b <= Math.Min(maxSystemNumber, system + emptySystemsBuffer); b++) {
											if (b == system) {
												continue;
											}
											GalaxyInfo bufferSystem = await GetGalaxyInfoCached(new Coordinate((int) t.Galaxy, b, 1, Celestials.Planet));
											if (await BufferSystemBlocked(bufferSystem)) {
												return (false, b);
											}
										}
										return (true, 0);
									}

									int targetsBeforeThisRange = targets.Count;
									// Scanning stops once enough candidates are found, since a full sweep of a wide range can blow past the worker's stuck-detection window; a few extra candidates are kept as margin.
									int neededForThisRange = maxPlanetsInThisRange - planetsInThisRange;
									int candidateSafetyMargin = Math.Max(5, neededForThisRange * 3);
									for (int i = (int) t.StartSystem; i <= (int) t.EndSystem; i++) {
										if (targets.Count - targetsBeforeThisRange >= candidateSafetyMargin) {
											DoLog(LogLevel.Debug, $"Enough candidates found for range [{t.Galaxy}:{t.StartSystem}-{t.EndSystem}:{t.StartPosition}-{t.EndPosition}] ({targets.Count - targetsBeforeThisRange}/{candidateSafetyMargin}) - stopping scan early at system {i}.");
											break;
										}

										if (excludeSystems.Contains(i)) {
											continue;
										}

										if (ShouldExcludeSystem((int) t.Galaxy, i)) {
											continue;
										}

										if (targetEmptySystems) {
											GalaxyInfo candidateSystem = await GetGalaxyInfoCached(new Coordinate((int) t.Galaxy, i, 1, Celestials.Planet));
											bool systemBlocked = false;
											foreach (var candidatePlanet in candidateSystem.Planets) {
												if (await IsBlockingPlanet(candidatePlanet)) {
													systemBlocked = true;
													break;
												}
											}
											if (systemBlocked) {
												continue;
											}
										}

										if (emptySystemsBuffer > 0) {
											var bufferResult = await HasEmptyBuffer(i);
											if (!bufferResult.Ok) {
												DoLog(LogLevel.Debug, $"Skipping system {t.Galaxy}:{i}: required {emptySystemsBuffer} empty system(s) buffer not satisfied, blocked at {t.Galaxy}:{bufferResult.BlockedAt} (occupied by another player).");
												// Every candidate within the buffer distance of a blocking system fails for the same reason, so the loop jumps past its shadow instead of re-testing each one.
												i = bufferResult.BlockedAt + emptySystemsBuffer;
												continue;
											}
										}

										for (int ii = (int) t.StartPosition; ii <= (int) t.EndPosition; ii++) {
											Coordinate targetCoords = new(
												(int) t.Galaxy,
												(int) i,
												(int) ii,
												Celestials.Planet
											);
											if (ShouldExcludeTarget(targetCoords)) {
												continue;
											}

											if (_calculationService.IsAstrophysicsPositionValid((int) targetCoords.Position, (int) _tbotInstance.UserData.researches.Astrophysics)) {
												targets.Add(targetCoords);
												targetRangeIndex[targetCoords] = rangeIdx;
											}
										}
									}

									int candidatesFoundThisRange = targets.Count - targetsBeforeThisRange;
									DoLog(LogLevel.Information, $"Scan of range [{t.Galaxy}:{t.StartSystem}-{t.EndSystem}:{t.StartPosition}-{t.EndPosition}] complete: {candidatesFoundThisRange} valid candidate coordinate(s) found.");
								}
								List<Coordinate> filteredTargets = new();
								foreach (Coordinate t in targets) {
									if (_tbotInstance.UserData.celestials.Any(c => c.HasCoords(t))) {
										continue;
									}
									GalaxyInfo galaxy = await GetGalaxyInfoCached(t);
									if (galaxy.Planets.Any(p => p != null && p.HasCoords(t))) {
										continue;
									}
									filteredTargets.Add(t);
								}
								if (filteredTargets.Count() > 0) {
									if ((bool) _tbotInstance.InstanceSettings.AutoColonize.RandomPosition) {
										List<Coordinate> filteredTargetsRdm = new();
										var random = new Random();
										for (int i = 0; i < filteredTargets.Count; i++) {
											int index = random.Next(filteredTargets.Count);
											filteredTargetsRdm.Add(filteredTargets[index]);
											filteredTargets.RemoveAt(index);
										}
										filteredTargets = (bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active
											? filteredTargetsRdm
												.Take(MaxSlots)
												.ToList()
											: filteredTargetsRdm
												.Take(maxPlanets - currentPlanets)
												.ToList();
									} else {
										filteredTargets = (bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active
											? filteredTargets
												.OrderBy(t => _calculationService.CalcDistance(origin.Coordinate, t, _tbotInstance.UserData.serverData))
												.Take(MaxSlots)
												.ToList()
											: filteredTargets
												.OrderBy(t => _calculationService.CalcDistance(origin.Coordinate, t, _tbotInstance.UserData.serverData))
												.Take(maxPlanets - currentPlanets)
												.ToList();
									}
									Ships ships = new() { ColonyShip = 1 };
									filteredTargets = filteredTargets
										.OrderBy(t => _calculationService.CalcFleetPrediction(origin, t, ships, Missions.Colonize, Speeds.HundredPercent, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass).Time)
										.ToList();
									int indexList = 0;
									// Colony ships left at the origin are tracked as they are sent, since the ship count is never refreshed and the loop would otherwise keep trying to send a ship it no longer has.
									long colonyShipsRemaining = origin.Ships.ColonyShip;
									foreach (var target in filteredTargets) {
										indexList++;
										if (colonyShipsRemaining < 1) {
											_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"No more colony ships available at {origin.ToString()} for the remaining targets this cycle - stopping.");
											break;
										}
										if (targetRangeIndex.TryGetValue(target, out int targetRangeIdx) && rangeRemainingQuota.TryGetValue(targetRangeIdx, out int quotaLeft) && quotaLeft <= 0) {
											_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Skipping {target.ToString()}: its range's MaxPlanets quota was already filled by an earlier target this same cycle.");
											continue;
										}
										_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
										var colonize = _tbotInstance.UserData.fleets
											.Where(f => f.Mission == Missions.Colonize)
											.Where(f => f.ReturnFlight == false)
											.Where(f => f.Destination.Galaxy == target.Galaxy)
											.Where(f => f.Destination.System == target.System)
											.Where(f => f.Destination.Position == target.Position)
											.Count();
										
										if (colonize > 0) {
											_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Skipping colonize: there is already a colonize incoming in {target.ToString()}");
											continue;
										}

										// Final live check before committing the colony ship: the earlier filtering may have used cached data and someone else may have taken the spot meanwhile.
										GalaxyInfo freshCheck = await GetGalaxyInfoWithRetry(target);
										if (freshCheck.Planets.Any(p => p != null && p.HasCoords(target))) {
											DoLog(LogLevel.Information, $"Skipping colonize: {target.ToString()} was just taken by someone else, re-checked right before sending.");
											continue;
										}

										{
											DoLog(LogLevel.Debug, "Send Colonize.");
											var fleetId = await _fleetScheduler.SendFleet(origin, ships, target, Missions.Colonize, Speeds.HundredPercent, null, _tbotInstance.UserData.userInfo.Class, false, true);
											if (fleetId > (int) SendFleetCode.GenericError) {
												colonyShipsRemaining--;
												if (targetRangeIndex.TryGetValue(target, out int sentRangeIdx) && rangeRemainingQuota.ContainsKey(sentRangeIdx)) {
													rangeRemainingQuota[sentRangeIdx]--;
												}
											}
											_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
											List<Fleet> orderedFleet = _tbotInstance.UserData.fleets
												.Where(fleet => fleet.Mission == Missions.Colonize)
												.ToList();
											orderedFleet = (bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active
												? orderedFleet
													.OrderBy(fleet => fleet.ArriveIn)
													.ToList()
												: orderedFleet
													.OrderByDescending(fleet => fleet.ArriveIn)
													.ToList();
											if (orderedFleet.Count() > 0) {
												interval = (int) ((1000 * orderedFleet.First().ArriveIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
											}

											if (fleetId == (int) SendFleetCode.AfterSleepTime) {
												stop = true;
												return;
											}
											if (fleetId == (int) SendFleetCode.NotEnoughDeuterium) {
												_tbotInstance.log(LogLevel.Warning, LogSender.Colonize, $"Not enough deuterium to colonize {target}. Skipping this target.");
												continue;
											}
											if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
												long delayInterval = 0;
												try {
													var returningFleets = _tbotInstance.UserData.fleets.Where(f => f.BackIn.HasValue).ToList();
													delayInterval = returningFleets.Any()
														? (returningFleets.Min(f => f.BackIn.Value) * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds)
														: RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMax);
												} catch {
													delayInterval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMax);
												}
												_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Not enough fleet slots available. Delaying for {TimeSpan.FromMilliseconds(delayInterval).TotalSeconds}s.");
												await Task.Delay((int)delayInterval);
												return;
											}
											var minWaitNextFleet = (int) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MinWaitNextFleet;
											var maxWaitNextFleet = (int) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxWaitNextFleet;
											
											if (minWaitNextFleet < 0)
												minWaitNextFleet = 0;
											if (maxWaitNextFleet < 1)
												maxWaitNextFleet = 1;
											
											var rndWaitTimeMs = 0;	//(int) RandomizeHelper.CalcRandomIntervalSecToMs(minWaitNextFleet, maxWaitNextFleet);
											if (indexList < filteredTargets.Count()) {
												Coordinate nextSlot = filteredTargets.ElementAt(indexList);
												rndWaitTimeMs = _calculationService.CalcFleetPrediction(origin, nextSlot, ships, Missions.Colonize, Speeds.HundredPercent, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass).Time - _calculationService.CalcFleetPrediction(origin, target, ships, Missions.Colonize, Speeds.HundredPercent, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass).Time < maxWaitNextFleet ? 
													(int) RandomizeHelper.CalcRandomIntervalSecToMs(minWaitNextFleet, maxWaitNextFleet) :
													0;
											}

											

											DoLog(LogLevel.Information, $"Wait {((float) rndWaitTimeMs / 1000).ToString("0.00")}s for next Colonization");
											await Task.Delay(rndWaitTimeMs, _ct);
										}
									}
								} else {
									_tbotInstance.log(LogLevel.Information, LogSender.Colonize, "No valid coordinate in target list.");
								}
							} else {
								await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Productions);
								await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Facilities);
								if (origin.Productions.Any()) {
									_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"{neededColonizers} colony ship(s) needed. {origin.Productions.Where(p => p.ID == (int) Buildables.ColonyShip).Sum(p => p.Nbr)} colony ship(s) already in production.");
									foreach (var prod in origin.Productions) {
										if (prod == origin.Productions.First()) {
											interval += (int) _calculationService.CalcProductionTime((Buildables) prod.ID, prod.Nbr - 1, _tbotInstance.UserData.serverData, origin.Facilities) * 1000;
										} else {
											interval += (int) _calculationService.CalcProductionTime((Buildables) prod.ID, prod.Nbr, _tbotInstance.UserData.serverData, origin.Facilities) * 1000;
										}
										if (prod.ID == (int) Buildables.ColonyShip) {
											break;
										}
									}
								} else {
									_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"{neededColonizers} colony ship(s) needed.");
									await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Resources);
									var cost = _calculationService.CalcPrice(Buildables.ColonyShip, neededColonizers - (int) origin.Ships.ColonyShip);
									if (origin.Resources.IsEnoughFor(cost)) {
										await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Constructions);
										if (origin.HasConstruction() && (origin.Constructions.BuildingID == (int) Buildables.Shipyard || origin.Constructions.BuildingID == (int) Buildables.NaniteFactory)) {
											_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Unable to build colony ship: {((Buildables) origin.Constructions.BuildingID).ToString()} is in construction");
											interval = (long) origin.Constructions.BuildingCountdown * (long) 1000;
										} else if (origin.HasProduction()) {
											_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Unable to build colony ship: there is already something in production");
											interval = (long) _calculationService.CalcProductionTime((Buildables) origin.Productions.First().ID, origin.Productions.First().Nbr - 1, _tbotInstance.UserData.serverData, origin.Facilities) * 1000;
										} else if (origin.Facilities.Shipyard >= 4 && _tbotInstance.UserData.researches.ImpulseDrive >= 3) {
											_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Building {neededColonizers - origin.Ships.ColonyShip}....");
											await _ogameService.BuildShips(origin, Buildables.ColonyShip, neededColonizers - origin.Ships.ColonyShip);
											interval = (int) _calculationService.CalcProductionTime(Buildables.ColonyShip, neededColonizers - (int) origin.Ships.ColonyShip, _tbotInstance.UserData.serverData, origin.Facilities) * 1000;
										} else {
											_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Requirements to build colony ship not met");
										}
									} else {
										_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Not enough resources to build {neededColonizers} colony ship(s). Needed: {cost.TransportableResources} - Available: {origin.Resources.TransportableResources}");
									}
								}
							}
						}
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.Colonize, "No new planet is needed.");
					}

					DateTime time = await _tbotOgameBridge.GetDateTime();
					if (interval <= 0) {
						interval = RandomizeHelper.CalcRandomInterval(IntervalType.AMinuteOrTwo);
					}

					DateTime newTime = time.AddMilliseconds(interval);
					ChangeWorkerPeriod(interval);
					_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Next check at {newTime}");
					await _tbotOgameBridge.CheckCelestials();
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Warning, LogSender.Colonize, $"HandleColonize exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.Colonize, $"Stacktrace: {e.StackTrace}");
				long interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoColonize.CheckIntervalMax);
				DateTime time = await _tbotOgameBridge.GetDateTime();
				if (interval <= 0)
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(interval);
				_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Next check at {newTime}");
			} finally {
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop) {
						_tbotInstance.log(LogLevel.Information, LogSender.Colonize, $"Stopping feature.");
						await EndExecution();
					}
					
					await _tbotOgameBridge.CheckCelestials();
				}
			}
		}
	}
}
