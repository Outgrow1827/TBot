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
		// OriginSystem anchors the alternating right/left walk (origin, origin+1,
		// origin-1, origin+2, origin-2, ...); Step counts how many hops from the
		// origin the cursor currently sits at, wrapping every maxSystem steps so
		// the whole galaxy is eventually covered symmetrically on both sides.
		public int OriginSystem { get; private set; }
		public int Step { get; private set; }
		public int System { get; private set; }
		public int NextPosition { get; private set; }

		public DiscoveryCursorState(int originSystem, int step = 0, int nextPosition = 1) {
			OriginSystem = originSystem;
			Step = step;
			NextPosition = nextPosition;
			System = originSystem;
		}

		public void Normalize(int fallbackSystem, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			if (OriginSystem < 1 || OriginSystem > maxSystem)
				OriginSystem = NormalizeSystem(fallbackSystem, maxSystem);

			if (Step < 0 || Step >= maxSystem)
				Step = 0;

			if (NextPosition < 1 || NextPosition > 15)
				NextPosition = 1;

			System = ComputeSystem(OriginSystem, Step, maxSystem);
		}

		public void CommitPosition(int system, int position, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			if (position >= 15) {
				AdvanceStep(maxSystem);
				NextPosition = 1;
				return;
			}

			NextPosition = Math.Max(1, position + 1);
		}

		public void SkipSystem(int system, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			AdvanceStep(maxSystem);
			NextPosition = 1;
		}

		private void AdvanceStep(int maxSystem) {
			Step = (Step + 1) % maxSystem;
			System = ComputeSystem(OriginSystem, Step, maxSystem);
		}

		private static int ComputeSystem(int origin, int step, int maxSystem) {
			if (step == 0)
				return NormalizeSystem(origin, maxSystem);

			int magnitude = (step + 1) / 2;
			int sign = step % 2 == 1 ? 1 : -1;
			return NormalizeSystem(origin + sign * magnitude, maxSystem);
		}

		private static int NormalizeSystem(int system, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;
			return ((system - 1) % maxSystem + maxSystem) % maxSystem + 1;
		}
	}
}
