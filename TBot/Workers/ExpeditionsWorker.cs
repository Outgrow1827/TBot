using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Common.Settings;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	public class ExpeditionsWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		private int _nextOriginIndex;

		public ExpeditionsWorker(
			ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) : base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		public override bool IsWorkerEnabledBySettings() {
			try { return (bool)_tbotInstance.InstanceSettings.Expeditions.Active; }
			catch (Exception) { return false; }
		}

		public override string GetWorkerName() => "Expeditions";
		public override Feature GetFeature() => Feature.Expeditions;
		public override LogSender GetLogSender() => LogSender.Expeditions;

		private int CountActiveExpeditionsFromOrigin(Celestial origin) {
			if (origin?.Coordinate == null || _tbotInstance.UserData.fleets == null)
				return 0;

			return _tbotInstance.UserData.fleets.Count(f =>
				f.Mission == Missions.Expedition &&
				f.Origin != null &&
				f.Origin.Galaxy == origin.Coordinate.Galaxy &&
				f.Origin.System == origin.Coordinate.System &&
				f.Origin.Position == origin.Coordinate.Position &&
				f.Origin.Type == origin.Coordinate.Type);
		}

		private List<Celestial> ResolveExpeditionOrigins() {
			var available = (_tbotInstance.UserData.celestials ?? new List<Celestial>())
				.Where(c => c?.Coordinate != null)
				.ToList();
			var origins = new List<Celestial>();

			if (_tbotInstance.InstanceSettings.Expeditions.Origin.Length > 0) {
				foreach (var configured in _tbotInstance.InstanceSettings.Expeditions.Origin) {
					try {
						var type = Enum.Parse<Celestials>(configured.Type.ToString(), true);
						var match = available.FirstOrDefault(c =>
							c.Coordinate.Galaxy == (int)configured.Galaxy &&
							c.Coordinate.System == (int)configured.System &&
							c.Coordinate.Position == (int)configured.Position &&
							c.Coordinate.Type == type);

						if (match == null) {
							DoLog(LogLevel.Warning,
								$"Expedition origin not found: {configured.Galaxy}:{configured.System}:{configured.Position} ({type})");
							continue;
						}

						if (!origins.Any(o => o.Coordinate.IsSame(match.Coordinate)))
							origins.Add(match);
					} catch (Exception e) {
						DoLog(LogLevel.Warning, $"Unable to parse expedition origin: {e.Message}");
					}
				}
			} else {
				// Automatic mode chooses one origin from the cached snapshot. It does not
				// refresh every celestial just to compare them.
				var best = available
					.OrderBy(c => c.Coordinate.Type == Celestials.Moon)
					.ThenByDescending(c => c.Ships == null ? 0 : _calculationService.CalcFleetCapacity(
						c.Ships,
						_tbotInstance.UserData.serverData,
						_tbotInstance.UserData.researches.HyperspaceTechnology,
						null,
						_tbotInstance.UserData.userInfo.Class,
						_tbotInstance.UserData.serverData.ProbeCargo))
					.FirstOrDefault();
				if (best != null)
					origins.Add(best);
			}

			return (bool)_tbotInstance.InstanceSettings.Expeditions.RandomizeOrder
				? origins.Shuffle().ToList()
				: origins;
		}

		private List<Celestial> FilterExpeditionCandidates(List<Celestial> origins) {
			// A cached empty fleet is useful information: do not open that origin just
			// to discover again that it cannot send. A null Ships value means that the
			// cache is incomplete, so the origin remains eligible for one live check.
			var candidates = origins
				.Where(origin => origin.Ships == null || !origin.Ships.GetMovableShips().IsEmpty())
				.ToList();

			foreach (var skipped in origins.Except(candidates))
				DoLog(LogLevel.Debug, $"Skipping {skipped.Coordinate}: no movable ships in the cached snapshot.");

			return candidates;
		}

		private Ships BuildManualExpeditionFleet() {
			return new Ships(
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.LightFighter,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.HeavyFighter,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Cruiser,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Battleship,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Battlecruiser,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Bomber,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Destroyer,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Deathstar,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.SmallCargo,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.LargeCargo,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.ColonyShip,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Recycler,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.EspionageProbe,
				0,
				0,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Reaper,
				(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Pathfinder);
		}

		private Ships BuildExpeditionFleet(Celestial origin, LFBonuses lfBonuses) {
			if ((bool)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Active)
				return BuildManualExpeditionFleet();

			Buildables primaryShip = Buildables.LargeCargo;
			if (!Enum.TryParse<Buildables>(_tbotInstance.InstanceSettings.Expeditions.PrimaryShip.ToString(), true, out primaryShip)) {
				DoLog(LogLevel.Warning, "Unable to parse PrimaryShip. Using LargeCargo.");
				primaryShip = Buildables.LargeCargo;
			}
			if (primaryShip == Buildables.Null)
				return new Ships();

			var availableShips = origin.Ships?.GetMovableShips() ?? new Ships();
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "PrimaryToKeep") &&
				(int)_tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep > 0) {
				availableShips.SetAmount(primaryShip, Math.Max(0,
					availableShips.GetAmount(primaryShip) - (long)_tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep));
			}

			return _calculationService.CalcFullExpeditionShips(
				availableShips,
				primaryShip,
				1,
				_tbotInstance.UserData.serverData,
				_tbotInstance.UserData.researches,
				lfBonuses,
				_tbotInstance.UserData.userInfo.Class,
				_tbotInstance.UserData.serverData.ProbeCargo);
		}

		private Coordinate BuildExpeditionDestination(Celestial origin, int expeditionCount, List<int> usedSystems, Random random) {
			if (!(bool)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Active) {
				return new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = origin.Coordinate.System,
					Position = 16,
					Type = Celestials.DeepSpace
				};
			}

			int range = Math.Max(1, (int)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Range);
			while (expeditionCount > range * 2)
				range++;

			for (int attempt = 0; attempt < 100; attempt++) {
				int system = GeneralHelper.WrapSystem(random.Next(
					origin.Coordinate.System - range,
					origin.Coordinate.System + range + 1));
				if (usedSystems.Contains(system))
					continue;

				usedSystems.Add(system);
				return new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = system,
					Position = 16,
					Type = Celestials.DeepSpace
				};
			}

			return new Coordinate {
				Galaxy = origin.Coordinate.Galaxy,
				System = origin.Coordinate.System,
				Position = 16,
				Type = Celestials.DeepSpace
			};
		}

		protected override async Task Execute() {
			bool stop = false;
			bool delay = false;

			try {
				if (!(bool)_tbotInstance.InstanceSettings.Expeditions.Active)
					return;

				_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
				if (_tbotInstance.UserData.researches.Astrophysics == 0) {
					DoLog(LogLevel.Information, "Skipping: Astrophysics not yet researched!");
					ChangeWorkerPeriod(RandomizeHelper.CalcRandomInterval(IntervalType.AboutHalfAnHour));
					return;
				}

				// This is the only account-wide snapshot phase. No origin is selected here.
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				_tbotInstance.UserData.serverData = await _ogameService.GetServerData();

				var rankSlotsPriority = new List<RankSlotsPriority> {
					new RankSlotsPriority(Feature.BrainAutoMine,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
						(bool)_tbotInstance.InstanceSettings.Brain.Active && (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active &&
						((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active),
						(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
					new RankSlotsPriority(Feature.Expeditions,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
						true,
						(int)_tbotInstance.UserData.slots.ExpTotal,
						(int)_tbotInstance.UserData.slots.ExpInUse),
					new RankSlotsPriority(Feature.AutoFarm,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
						(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
						(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)),
					new RankSlotsPriority(Feature.Colonize,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ? (int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots : 1,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
					new RankSlotsPriority(Feature.AutoDiscovery,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
						(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
						(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
					new RankSlotsPriority(Feature.Harvest,
						(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
						(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
						(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
				};

				int maxSlots = Math.Max(0,
					_tbotInstance.UserData.slots.Total - (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree);
				int expeditionsToSend = Math.Min(
					Math.Min(_tbotInstance.UserData.slots.ExpFree, _tbotInstance.UserData.slots.Free),
					maxSlots);

				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForAllExpeditions") &&
					(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions &&
					_tbotInstance.UserData.slots.ExpInUse > 0)
					expeditionsToSend = 0;

				if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForMajorityOfExpeditions") &&
					(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions &&
					(double)expeditionsToSend < Math.Round((double)_tbotInstance.UserData.slots.ExpTotal / 2D, 0, MidpointRounding.ToZero) + 1D)
					expeditionsToSend = 0;

				if (expeditionsToSend > 0) {
					int prioritySlots = _calculationService.CalcSlotsPriority(
						Feature.Expeditions,
						rankSlotsPriority,
						_tbotInstance.UserData.slots,
						_tbotInstance.UserData.fleets,
						(int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree);
					expeditionsToSend = Math.Min(expeditionsToSend, Math.Max(0, prioritySlots));
				}

				if (expeditionsToSend <= 0) {
					delay = true;
					return;
				}

				var origins = ResolveExpeditionOrigins();
				if (origins.Count == 0) {
					DoLog(LogLevel.Warning, "No valid expedition origin is configured.");
					delay = true;
					return;
				}

				origins = FilterExpeditionCandidates(origins);
				if (origins.Count == 0) {
					DoLog(LogLevel.Information, "No configured expedition origin currently has movable ships.");
					delay = true;
					return;
				}

				// LF bonuses are account-wide. They must not be refreshed once per origin.
				var lfBonuses = await _ogameService.GetLFBonuses();
				int maxPerOrigin = (int?)_tbotInstance.InstanceSettings.Expeditions.MaxExpeditionsPerOrigin ?? 1;
				var capacities = origins
					.Select(origin => Math.Max(0, maxPerOrigin - CountActiveExpeditionsFromOrigin(origin)))
					.ToArray();
				var sentByOrigin = new int[origins.Count];
				var unavailableOrigins = new bool[origins.Count];
				var remaining = expeditionsToSend;
				var planningStart = _nextOriginIndex % origins.Count;
				var random = new Random();
				var usedSystems = new List<int>();

			while (remaining > 0) {
				// Rebuild the plan after every failed origin. This is what lets an
				// origin with spare capacity take over instead of losing a slot.
				var residualCapacities = capacities
					.Select((capacity, index) => unavailableOrigins[index]
						? 0
						: Math.Max(0, capacity - sentByOrigin[index]))
					.ToArray();
				var plan = ExpeditionOriginPlanner.BuildRoundRobinPlan(residualCapacities, remaining, planningStart);
				if (plan.Sum() == 0)
					break;

				var progressInPass = false;
				for (var offset = 0; offset < origins.Count && remaining > 0; offset++) {
					var originIndex = (planningStart + offset) % origins.Count;
					var count = plan[originIndex];
					if (count <= 0)
						continue;

					var origin = origins[originIndex];
					for (var attempt = 0; attempt < count && remaining > 0; attempt++) {
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						if (CountActiveExpeditionsFromOrigin(origin) >= maxPerOrigin)
							break;

						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						if (_tbotInstance.UserData.slots.ExpFree <= 0 ||
							_tbotInstance.UserData.slots.Free <= (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
							delay = true;
							return;
						}

						// Only an origin that is about to send is refreshed. Multiple
						// sends from the same origin are real activity and are expected.
						var originUpdated = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Ships);
						var fleet = BuildExpeditionFleet(originUpdated, lfBonuses);
						if (fleet == null || fleet.IsEmpty() || originUpdated.Ships == null || !originUpdated.Ships.HasAtLeast(fleet, 1)) {
							DoLog(LogLevel.Information, $"No usable expedition fleet on {originUpdated.Coordinate}; excluding this origin for the rest of the cycle.");
							unavailableOrigins[originIndex] = true;
							break;
						}

						var destination = BuildExpeditionDestination(originUpdated, remaining, usedSystems, random);
						var payload = new Resources();
						if ((long)_tbotInstance.InstanceSettings.Expeditions.FuelToCarry > 0)
							payload.Deuterium = (long)_tbotInstance.InstanceSettings.Expeditions.FuelToCarry;

						DoLog(LogLevel.Information, $"Sending expedition from {originUpdated.Coordinate} to {destination}");
						var fleetId = await _fleetScheduler.SendFleet(
							originUpdated,
							fleet,
							destination,
							Missions.Expedition,
							Speeds.HundredPercent,
							payload);

						if (fleetId == (int)SendFleetCode.AfterSleepTime) {
							stop = true;
							return;
						}
						if (fleetId == (int)SendFleetCode.NotEnoughSlots) {
							delay = true;
							return;
						}
						if (fleetId <= (int)SendFleetCode.GenericError) {
							DoLog(LogLevel.Warning, $"Expedition was not sent from {originUpdated.Coordinate}; excluding this origin for the rest of the cycle.");
							unavailableOrigins[originIndex] = true;
							break;
						}

						sentByOrigin[originIndex]++;
						remaining--;
						progressInPass = true;
						planningStart = (originIndex + 1) % origins.Count;

						int minWait = (int)_tbotInstance.InstanceSettings.Expeditions.MinWaitNextFleet;
						int maxWait = (int)_tbotInstance.InstanceSettings.Expeditions.MaxWaitNextFleet;
						if (maxWait < minWait)
							(minWait, maxWait) = (maxWait, minWait);
						await Task.Delay((int)RandomizeHelper.CalcRandomIntervalSecToMs(minWait, maxWait), _ct);
					}
				}

				if (!progressInPass)
					break;
			}

			_nextOriginIndex = planningStart;

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				var expeditionFleets = _tbotInstance.UserData.fleets
					.Where(f => f.Mission == Missions.Expedition)
					.OrderBy(f => f.BackIn)
					.ToList();
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();

				long interval;
				if (expeditionFleets.Count == 0 ||
					(_tbotInstance.UserData.slots.ExpFree > 0 &&
					 !(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions &&
					 !(bool)_tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions)) {
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.AboutFiveMinutes);
				} else {
					interval = (int)((1000 * expeditionFleets.First().BackIn) +
						RandomizeHelper.CalcRandomIntervalSecToMs(
							(int)_tbotInstance.InstanceSettings.Expeditions.MinWaitNextRound,
							(int)_tbotInstance.InstanceSettings.Expeditions.MaxWaitNextRound));
				}

				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next expedition check at {DateTime.Now.AddMilliseconds(interval)}");
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"HandleExpeditions exception: {e.Message}");
				DoLog(LogLevel.Debug, e.StackTrace);
				ChangeWorkerPeriod(RandomizeHelper.CalcRandomInterval(IntervalType.AMinuteOrTwo));
			} finally {
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop)
						await EndExecution();

					if (delay) {
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						long interval;
						try {
							interval = (_tbotInstance.UserData.fleets.OrderBy(f => f.BackIn).First().BackIn ?? 0) * 1000 +
								RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} catch {
							interval = RandomizeHelper.CalcRandomInterval(
								(int)_tbotInstance.InstanceSettings.Expeditions.CheckIntervalMin,
								(int)_tbotInstance.InstanceSettings.Expeditions.CheckIntervalMax);
						}
						ChangeWorkerPeriod(interval);
					}

					await _tbotOgameBridge.CheckCelestials();
				}
			}
		}
	}
}
