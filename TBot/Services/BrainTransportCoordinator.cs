using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Services {
	// The 4 Brain workers share Brain.Transports.MaxSlots as one pool; the lock keeps them from
	// spending the same origin's resources twice.
	public static class BrainTransportCoordinator {
		// One lock per instance (account), not one process-wide: the 4 Brain workers of the SAME account
		// must serialize against each other, but two different accounts running in the same process
		// never share resources, so a single static lock only made one account's slow/stalled worker
		// block every other account's Brain cycle for up to ResourceDecisionLockTimeout.
		private static readonly ConcurrentDictionary<string, SemaphoreSlim> _resourceDecisionLocks = new();
		public static SemaphoreSlim GetResourceDecisionLock(string instanceAlias) =>
			_resourceDecisionLocks.GetOrAdd(instanceAlias ?? "", _ => new SemaphoreSlim(1, 1));

		// A network call stuck inside one worker's locked section (no response, no error - the
		// kind of silent connection stall seen against Gameforge's servers) would otherwise hold
		// this semaphore forever, permanently blocking the other 3 Brain workers too and, since
		// they're awaited from the same scheduling loop, potentially freezing the whole app
		// (observed 2026-08-05: console stopped responding, had to be force-closed). Bound the
		// wait so a stuck worker can only ever cost the others this long, not forever.
		public static readonly TimeSpan ResourceDecisionLockTimeout = TimeSpan.FromMinutes(10);

		public const int MaxStockpileSteps = 10;
		public const int MaxStockpileCostFactor = 3;

		// Sizes a transport for the constructions the worker would really queue during the round trip.
		// `plan` is the worker's own simulated sequence (first item = the one about to be built).
		public static Resources CalcResourcesForRoundTrip(
			ICalculationService calculationService,
			Celestial celestial,
			Coordinate origin,
			Buildables preferredShip,
			Researches researches,
			ServerData serverData,
			CharacterClass playerClass,
			AllianceClass allianceClass,
			IEnumerable<(Resources Cost, long Seconds)> plan,
			bool widenStockpileWindow = false) {

			var timeShips = new Ships();
			timeShips.Add(preferredShip, 1);
			long flightWindowSeconds = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, timeShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			).Time * (widenStockpileWindow ? 3 : 2);

			// cost cap keeps the 1.5x-per-level growth from letting far, unlikely steps dominate the total
			Resources total = new();
			long elapsed = 0, costCap = 0;
			int steps = 0;
			foreach (var (cost, seconds) in plan) {
				if (steps == 0)
					costCap = cost.TotalResources * MaxStockpileCostFactor;
				else if (elapsed >= flightWindowSeconds || steps > MaxStockpileSteps || total.TotalResources + cost.TotalResources > costCap)
					break;
				total = total.Sum(cost);
				elapsed += seconds;
				steps++;
			}

			long shipsNeeded = calculationService.CalcShipNumberForPayload(total, preferredShip, researches.HyperspaceTechnology, serverData, 0, playerClass, 0);
			var fuelShips = new Ships();
			fuelShips.Add(preferredShip, shipsNeeded);
			long roundTripFuel = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, fuelShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			).Fuel * 2;
			return total.Sum(new Resources(0, 0, roundTripFuel));
		}

		// Deep copy so a plan can simulate levels without touching the shared celestial.
		public static T Clone<T>(T source) =>
			(T) Newtonsoft.Json.JsonConvert.DeserializeObject(Newtonsoft.Json.JsonConvert.SerializeObject(source), source.GetType());
	}
}