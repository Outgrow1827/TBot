using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public sealed record AutoFarmSlotBudget(int MaxSlots, int OwnedSlots, int AvailableSlots);

	/// <summary>
	/// Calculates the AutoFarm worker's fleet-slot budget without confusing
	/// AutoFarm fleets with unrelated mixed fleets.
	/// </summary>
	public static class AutoFarmSlotPlanner {
		public static AutoFarmSlotBudget Calculate(
			int maxSlots,
			int totalFreeSlots,
			int slotsToLeaveFree,
			IEnumerable<Fleet> fleets,
			Buildables cargoType) {
			var ownedSlots = (fleets ?? Enumerable.Empty<Fleet>())
				.Count(fleet => IsOwnedFleet(fleet, cargoType));

			var availableByWorker = Math.Max(0, maxSlots - ownedSlots);
			var availableGlobally = Math.Max(0, totalFreeSlots - slotsToLeaveFree);

			return new AutoFarmSlotBudget(
				Math.Max(0, maxSlots),
				ownedSlots,
				Math.Min(availableByWorker, availableGlobally));
		}

		public static bool IsOwnedFleet(Fleet fleet, Buildables cargoType) {
			if (fleet?.Ships == null)
				return false;

			if (fleet.Mission == Missions.Spy)
				return fleet.Ships.IsOnlyProbes();

			return fleet.Mission == Missions.Attack && IsOnlyShip(fleet.Ships, cargoType);
		}

		private static bool IsOnlyShip(Ships ships, Buildables shipType) {
			if (shipType == Buildables.Null || ships.GetAmount(shipType) <= 0)
				return false;

			return typeof(Ships)
				.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(property => property.PropertyType == typeof(long))
				.Where(property => !string.Equals(property.Name, shipType.ToString(), StringComparison.Ordinal))
				.All(property => (long)property.GetValue(ships) == 0);
		}
	}
}
