using System;
using System.Threading;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Services {
	// AutoMine, AutoResearch, LifeformAutoMine, and LifeformAutoResearch each run as their own
	// independent, concurrently-scheduled worker, but all four share a single Brain.Transports.MaxSlots
	// budget and independently read/decide/spend resources from the same origin celestials with no
	// coordination between them. That causes two symptoms: (1) whichever one claims a transport first
	// monopolizes the whole shared slot count until its fleet returns, starving the other three, and
	// (2) two of them can simultaneously commit to spending the same origin's resources for different
	// purposes, since neither sees the other's in-flight decision.
	//
	// This coordinator fixes both: GetMaxSlotsForActiveItem splits the configured MaxSlots evenly
	// across whichever of the 4 items are currently active (recomputed every call, so toggling an
	// item on/off at runtime updates the split immediately), and ResourceDecisionLock serializes the
	// "check what's needed -> send transport" critical section across all 4 workers so they can never
	// double-spend the same resources.
	public static class BrainTransportCoordinator {
		public static readonly SemaphoreSlim ResourceDecisionLock = new SemaphoreSlim(1, 1);

		// A network call stuck inside one worker's locked section (no response, no error - the
		// kind of silent connection stall seen against Gameforge's servers) would otherwise hold
		// this semaphore forever, permanently blocking the other 3 Brain workers too and, since
		// they're awaited from the same scheduling loop, potentially freezing the whole app
		// (observed 2026-08-05: console stopped responding, had to be force-closed). Bound the
		// wait so a stuck worker can only ever cost the others this long, not forever.
		public static readonly TimeSpan ResourceDecisionLockTimeout = TimeSpan.FromMinutes(10);

		public static int GetMaxSlotsForActiveItem(dynamic instanceSettings, int totalMaxSlots) {
			int activeCount = 0;
			try { if ((bool) instanceSettings.Brain.AutoMine.Active) activeCount++; } catch { }
			try { if ((bool) instanceSettings.Brain.AutoResearch.Active) activeCount++; } catch { }
			try { if ((bool) instanceSettings.Brain.LifeformAutoMine.Active) activeCount++; } catch { }
			try { if ((bool) instanceSettings.Brain.LifeformAutoResearch.Active) activeCount++; } catch { }

			if (activeCount <= 1)
				return totalMaxSlots;

			return Math.Max(1, totalMaxSlots / activeCount);
		}

		// Sizes a transport to cover every level of `buildable` that would sequentially finish
		// building on `celestial` while the fleet makes its round trip from `origin`, instead of
		// just the next level - so the fleet doesn't need a second trip mid-queue. Starts counting
		// from `startingLevel` (the level about to be queued) and keeps adding subsequent levels'
		// cost until their cumulative build time would exceed the round-trip flight time.
		// Includes the deuterium fuel cost for the round-trip transport.
		// For ships (SolarSatellite/Crawler), `startingLevel` is the TOTAL quantity needed — they are
		// queued all at once, not built sequentially level by level. So we do NOT add extra units.
		public static Resources CalcResourcesForRoundTrip(
			ICalculationService calculationService,
			Celestial celestial,
			Coordinate origin,
			Buildables preferredShip,
			Buildables buildable,
			int startingLevel,
			Researches researches,
			ServerData serverData,
			Facilities facilities,
			CharacterClass playerClass,
			AllianceClass allianceClass,
			int cumulativeLabLevel = 0) {

			var timeShips = new Ships();
			timeShips.Add(preferredShip, 1);
			var fleetPrediction = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, timeShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			);
			long flightWindowSeconds = fleetPrediction.Time * 2;

			bool isShip = buildable == Buildables.SolarSatellite || buildable == Buildables.Crawler;

			Resources total = calculationService.CalcPrice(buildable, startingLevel, celestial.LFBonuses);
			long elapsed = isShip
				? calculationService.CalcProductionTime(buildable, 1, serverData, facilities, cumulativeLabLevel) * startingLevel
				: calculationService.CalcProductionTime(buildable, startingLevel, serverData, facilities, cumulativeLabLevel);

			if (!isShip) {
				int level = startingLevel;
				int levelsAdded = 0;
				while (elapsed < flightWindowSeconds && levelsAdded < 10) {
					level++;
					total = total.Sum(calculationService.CalcPrice(buildable, level, celestial.LFBonuses));
					elapsed += calculationService.CalcProductionTime(buildable, level, serverData, facilities, cumulativeLabLevel);
					levelsAdded++;
				}
			}

			long shipsNeeded = calculationService.CalcShipNumberForPayload(total, preferredShip, researches.HyperspaceTechnology, serverData, 0, playerClass, 0);
			var fuelShips = new Ships();
			fuelShips.Add(preferredShip, shipsNeeded);
			long roundTripFuel = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, fuelShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			).Fuel * 2;
			total = total.Sum(new Resources(0, 0, roundTripFuel));

			return total;
		}

		// Same idea as above, for Lifeform buildings.
		public static Resources CalcResourcesForRoundTrip(
			ICalculationService calculationService,
			Celestial celestial,
			Coordinate origin,
			Buildables preferredShip,
			LFBuildables buildable,
			int startingLevel,
			Researches researches,
			ServerData serverData,
			double costReduction,
			double energyCostReduction,
			double populationCostReduction,
			CharacterClass playerClass,
			AllianceClass allianceClass) {

			var timeShips = new Ships();
			timeShips.Add(preferredShip, 1);
			var fleetPrediction = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, timeShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			);
			long flightWindowSeconds = fleetPrediction.Time * 2;

			int level = startingLevel;
			Resources total = calculationService.CalcPrice(buildable, level, costReduction, energyCostReduction, populationCostReduction);
			long elapsed = calculationService.CalcProductionTime(buildable, level, serverData, celestial);

			int levelsAdded = 0;
			while (elapsed < flightWindowSeconds && levelsAdded < 10) {
				level++;
				total = total.Sum(calculationService.CalcPrice(buildable, level, costReduction, energyCostReduction, populationCostReduction));
				elapsed += calculationService.CalcProductionTime(buildable, level, serverData, celestial);
				levelsAdded++;
			}

			long shipsNeeded = calculationService.CalcShipNumberForPayload(total, preferredShip, researches.HyperspaceTechnology, serverData, 0, playerClass, 0);
			var fuelShips = new Ships();
			fuelShips.Add(preferredShip, shipsNeeded);
			long roundTripFuel = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, fuelShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			).Fuel * 2;
			total = total.Sum(new Resources(0, 0, roundTripFuel));

			return total;
		}

		// Same idea as above, for Lifeform research.
		public static Resources CalcResourcesForRoundTrip(
			ICalculationService calculationService,
			Celestial celestial,
			Coordinate origin,
			Buildables preferredShip,
			LFTechno buildable,
			int startingLevel,
			Researches researches,
			ServerData serverData,
			double costReduction,
			CharacterClass playerClass,
			AllianceClass allianceClass) {

			var timeShips = new Ships();
			timeShips.Add(preferredShip, 1);
			var fleetPrediction = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, timeShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			);
			long flightWindowSeconds = fleetPrediction.Time * 2;

			int level = startingLevel;
			Resources total = calculationService.CalcPrice(buildable, level, costReduction);
			long elapsed = calculationService.CalcProductionTime(buildable, level, serverData, costReduction);

			int levelsAdded = 0;
			while (elapsed < flightWindowSeconds && levelsAdded < 10) {
				level++;
				total = total.Sum(calculationService.CalcPrice(buildable, level, costReduction));
				elapsed += calculationService.CalcProductionTime(buildable, level, serverData, costReduction);
				levelsAdded++;
			}

			long shipsNeeded = calculationService.CalcShipNumberForPayload(total, preferredShip, researches.HyperspaceTechnology, serverData, 0, playerClass, 0);
			var fuelShips = new Ships();
			fuelShips.Add(preferredShip, shipsNeeded);
			long roundTripFuel = calculationService.CalcFleetPrediction(
				origin, celestial.Coordinate, fuelShips, Missions.Transport,
				Speeds.HundredPercent, researches, serverData, celestial.LFBonuses, playerClass, allianceClass
			).Fuel * 2;
			total = total.Sum(new Resources(0, 0, roundTripFuel));

			return total;
		}
	}
}
