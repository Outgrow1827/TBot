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
		// One lock per account, not process-wide: two accounts in the same process never share
		// resources, so one account's stalled worker must not block the others' cycles.
		private static readonly ConcurrentDictionary<string, SemaphoreSlim> _resourceDecisionLocks = new();
		public static SemaphoreSlim GetResourceDecisionLock(string instanceAlias) =>
			_resourceDecisionLocks.GetOrAdd(instanceAlias ?? "", _ => new SemaphoreSlim(1, 1));

		// A network call stuck in the locked section would hold the semaphore forever, so the wait is
		// bounded: a stuck worker can only cost the others this long.
		public static readonly TimeSpan ResourceDecisionLockTimeout = TimeSpan.FromMinutes(10);

		// loop guard only; the window is time-based
		public const int MaxStockpileSteps = 50;

		// Resources for every construction the worker would queue while the fleet flies:
		// window = 2x the one-way flight (there and back),
		// never past the next sleep. `plan` is the worker's own simulated sequence.
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
			long secondsUntilSleep = long.MaxValue) {

			var timeShips = new Ships();
			timeShips.Add(preferredShip, 1);
			long flightWindowSeconds = Math.Min(secondsUntilSleep, calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, timeShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			).Time * 2);

			Resources total = new();
			long elapsed = 0;
			int steps = 0;
			foreach (var (cost, seconds) in plan) {
				if (steps > 0 && (elapsed >= flightWindowSeconds || steps >= MaxStockpileSteps))
					break;
				total = total.Sum(cost);
				elapsed += seconds;
				steps++;
			}
			// fuel is not added: it is paid from the origin's deuterium when the fleet leaves
			return total;
		}

		// Seconds until SleepMode's next GoToSleep; MaxValue when sleep mode is off or unparsable.
		public static long SecondsUntilSleep(dynamic instanceSettings, DateTime now) {
			try {
				if (!(bool) instanceSettings.SleepMode.Active || !DateTime.TryParse((string) instanceSettings.SleepMode.GoToSleep, out DateTime goToSleep))
					return long.MaxValue;
				DateTime next = now.Date + goToSleep.TimeOfDay;
				if (next <= now)
					next = next.AddDays(1);
				return (long) (next - now).TotalSeconds;
			} catch {
				return long.MaxValue;
			}
		}

		// Deep copy so a plan can simulate levels without touching the shared celestial.
		public static T Clone<T>(T source) =>
			(T) Newtonsoft.Json.JsonConvert.DeserializeObject(Newtonsoft.Json.JsonConvert.SerializeObject(source), source.GetType());
	}
}