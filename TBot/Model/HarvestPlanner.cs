using System;
using System.Collections.Generic;
using System.Linq;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public sealed class HarvestTarget {
		public HarvestTarget(Coordinate destination, Resources resources, bool isOwnDebris = false) {
			Destination = destination;
			Resources = resources;
			IsOwnDebris = isOwnDebris;
		}

		public Coordinate Destination { get; }
		public Resources Resources { get; }
		public bool IsOwnDebris { get; }
	}

	public sealed class HarvestOriginState {
		public HarvestOriginState(Celestial celestial) {
			Celestial = celestial ?? throw new ArgumentNullException(nameof(celestial));
			AvailableRecyclers = celestial.Ships?.Recycler ?? 0;
			AvailablePathfinders = celestial.Ships?.Pathfinder ?? 0;
		}

		public Celestial Celestial { get; }
		public long AvailableRecyclers { get; private set; }
		public long AvailablePathfinders { get; private set; }

		public long AvailableShips(Buildables ship) {
			return ship switch {
				Buildables.Recycler => AvailableRecyclers,
				Buildables.Pathfinder => AvailablePathfinders,
				_ => 0
			};
		}

		public void Reserve(Buildables ship, long amount) {
			if (amount <= 0)
				return;

			switch (ship) {
				case Buildables.Recycler:
					AvailableRecyclers = Math.Max(0, AvailableRecyclers - amount);
					break;
				case Buildables.Pathfinder:
					AvailablePathfinders = Math.Max(0, AvailablePathfinders - amount);
					break;
			}
		}
	}

	public sealed class HarvestAssignment {
		public HarvestAssignment(HarvestTarget target, HarvestOriginState origin, Buildables ship, long requiredShips, long shipsToSend, int distance) {
			Target = target;
			OriginState = origin;
			Ship = ship;
			RequiredShips = requiredShips;
			ShipsToSend = shipsToSend;
			Distance = distance;
		}

		public HarvestTarget Target { get; }
		public HarvestOriginState OriginState { get; }
		public Celestial Origin => OriginState.Celestial;
		public Buildables Ship { get; }
		public long RequiredShips { get; }
		public long ShipsToSend { get; }
		public int Distance { get; }
		public bool IsPartial => ShipsToSend < RequiredShips;
	}

	public static class HarvestPlanner {
		public static IReadOnlyList<HarvestAssignment> Build(
			IEnumerable<HarvestTarget> targets,
			IEnumerable<HarvestOriginState> origins,
			int maxSlots,
			Func<HarvestTarget, HarvestOriginState, long> requiredShips,
			Func<HarvestTarget, HarvestOriginState, int> distance = null) {
			if (maxSlots <= 0 || targets == null || origins == null || requiredShips == null)
				return Array.Empty<HarvestAssignment>();

			var uniqueTargets = new List<HarvestTarget>();
			foreach (var target in targets.ToList()) {
				if (target?.Destination == null || target.Resources == null || target.Resources.TotalResources <= 0)
					continue;
				if (!uniqueTargets.Any(existing => existing.Destination.IsSame(target.Destination)))
					uniqueTargets.Add(target);
			}

			var originStates = origins.Where(origin => origin?.Celestial?.Coordinate != null).ToList();
			var assignments = new List<HarvestAssignment>(Math.Min(maxSlots, uniqueTargets.Count));

			foreach (var target in uniqueTargets
				.OrderByDescending(candidate => candidate.IsOwnDebris)
				.ThenByDescending(candidate => candidate.Resources.TotalResources)
				.ThenBy(candidate => candidate.Destination.Galaxy)
				.ThenBy(candidate => candidate.Destination.System)
				.ThenBy(candidate => candidate.Destination.Position)) {
				if (assignments.Count >= maxSlots)
					break;

				var viableOrigins = originStates
					.Select(origin => new {
						Origin = origin,
						Required = Math.Max(0, requiredShips(target, origin)),
						Distance = Math.Max(0, distance?.Invoke(target, origin) ?? 0)
					})
					.Where(candidate => candidate.Required > 0 && candidate.Origin.AvailableShips(GetShipType(target)) > 0)
					.ToList();

				if (viableOrigins.Count == 0)
					continue;

				var fullCollectionOrigins = viableOrigins
					.Where(candidate => candidate.Origin.AvailableShips(GetShipType(target)) >= candidate.Required)
					.ToList();
				var selected = fullCollectionOrigins.Count > 0
					? fullCollectionOrigins
						.OrderBy(candidate => candidate.Distance)
						.ThenBy(candidate => candidate.Origin.Celestial.Coordinate.Type == Celestials.Moon ? 0 : 1)
						.ThenByDescending(candidate => candidate.Origin.AvailableShips(GetShipType(target)))
						.First()
					: viableOrigins
						.OrderByDescending(candidate => candidate.Origin.AvailableShips(GetShipType(target)))
						.ThenBy(candidate => candidate.Distance)
						.ThenBy(candidate => candidate.Origin.Celestial.Coordinate.Type == Celestials.Moon ? 0 : 1)
						.First();

				var ship = GetShipType(target);
				var shipsToSend = Math.Min(selected.Required, selected.Origin.AvailableShips(ship));
				if (shipsToSend <= 0)
					continue;

				selected.Origin.Reserve(ship, shipsToSend);
				assignments.Add(new HarvestAssignment(target, selected.Origin, ship, selected.Required, shipsToSend, selected.Distance));
			}

			return assignments;
		}

		private static Buildables GetShipType(HarvestTarget target) {
			return target.IsOwnDebris ? Buildables.Recycler : Buildables.Pathfinder;
		}
	}
}
