using System;
using System.Collections.Generic;
using System.Linq;

namespace TBot.Model {
	public static class AutoFarmScanPlanner {
		public static IReadOnlyList<int> EnumerateSystems(int startSystem, int endSystem, int resumeSystem) {
			if (endSystem < startSystem)
				return Array.Empty<int>();

			int count = endSystem - startSystem + 1;
			int normalizedResume = Math.Clamp(resumeSystem, startSystem, endSystem) - startSystem;
			return Enumerable.Range(0, count)
				.Select(offset => startSystem + ((normalizedResume + offset) % count))
				.ToArray();
		}
	}
}
