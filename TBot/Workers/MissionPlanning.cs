using System;
using System.Collections.Generic;
using System.Linq;

namespace Tbot.Workers {
	/// <summary>
	/// Pure planning primitives used by the fleet workers. They deliberately do not
	/// perform network calls or mutate bot state, which keeps the send phase small
	/// and makes the edge cases deterministic.
	/// </summary>
	public static class ExpeditionOriginPlanner {
		public static bool ShouldRetryUnavailableOrigins(
			int remaining,
			IReadOnlyList<bool> retryableUnavailableOrigins,
			bool alreadyRetried) {
			return remaining > 0
				&& !alreadyRetried
				&& retryableUnavailableOrigins != null
				&& retryableUnavailableOrigins.Any(value => value);
		}

		public static int[] BuildRoundRobinPlan(IReadOnlyList<int> capacities, int requested, int startIndex = 0) {
			if (capacities == null || capacities.Count == 0 || requested <= 0)
				return Array.Empty<int>();

			var plan = new int[capacities.Count];
			var remaining = requested;
			var index = NormalizeIndex(startIndex, capacities.Count);

			while (remaining > 0) {
				var progressed = false;
				for (var offset = 0; offset < capacities.Count && remaining > 0; offset++) {
					var current = (index + offset) % capacities.Count;
					var capacity = Math.Max(0, capacities[current]);
					if (plan[current] >= capacity)
						continue;

					plan[current]++;
					remaining--;
					progressed = true;
				}

				if (!progressed)
					break;

				index = (index + 1) % capacities.Count;
			}

			return plan;
		}

		private static int NormalizeIndex(int index, int count) {
			if (count <= 0)
				return 0;

			var normalized = index % count;
			return normalized < 0 ? normalized + count : normalized;
		}
	}

	/// <summary>
	/// Selects discovery positions reported as available by the system view.
	/// Positions not returned by that view must never be sent blindly.
	/// </summary>
	public static class DiscoverySystemPlanner {
		public static IReadOnlyList<int> SelectAvailablePositions(
			IEnumerable<int> availablePositions,
			int nextPosition,
			IReadOnlySet<int> blacklistedPositions,
			int maximum) {
			if (availablePositions == null || maximum <= 0)
				return Array.Empty<int>();

			var minimum = Math.Clamp(nextPosition, 1, 15);
			return availablePositions
				.Where(position => position >= minimum && position <= 15)
				.Where(position => blacklistedPositions == null || !blacklistedPositions.Contains(position))
				.Distinct()
				.OrderBy(position => position)
				.Take(maximum)
				.ToList();
		}
	}

	/// <summary>
	/// Cursor for one discovery origin. Every examined position must be committed,
	/// including blacklisted and rejected positions; otherwise the worker can loop
	/// forever on the last position of a system.
	/// </summary>
	public sealed class DiscoveryCursorState {
		public int System { get; private set; }
		public int NextPosition { get; private set; }

		public DiscoveryCursorState(int system, int nextPosition = 1) {
			System = system;
			NextPosition = nextPosition;
		}

		public void Normalize(int fallbackSystem, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			if (System < 1 || System > maxSystem)
				System = NormalizeSystem(fallbackSystem, maxSystem);

			if (NextPosition < 1 || NextPosition > 15)
				NextPosition = 1;
		}

		public void CommitPosition(int system, int position, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			if (position >= 15) {
				System = system >= maxSystem ? 1 : system + 1;
				NextPosition = 1;
				return;
			}

			System = NormalizeSystem(system, maxSystem);
			NextPosition = Math.Max(1, position + 1);
		}

		public void SkipSystem(int system, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			System = system >= maxSystem ? 1 : Math.Max(1, system + 1);
			NextPosition = 1;
		}

		private static int NormalizeSystem(int system, int maxSystem) {
			if (system < 1)
				return 1;
			if (system > maxSystem)
				return ((system - 1) % maxSystem) + 1;
			return system;
		}
	}
}
