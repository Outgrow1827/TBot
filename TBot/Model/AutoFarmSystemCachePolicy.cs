using System;

namespace TBot.Model {
	public enum AutoFarmSystemCacheDecision {
		Refresh,
		UseSnapshot,
		Skip
	}

	public static class AutoFarmSystemCachePolicy {
		public static AutoFarmSystemCacheDecision GetDecision(
			AutoFarmSystemSnapshot snapshot,
			DateTime nowUtc,
			TimeSpan dataTtl,
			TimeSpan emptySystemCooldown) {
			if (snapshot == null)
				return AutoFarmSystemCacheDecision.Refresh;

			var age = nowUtc - snapshot.ObservedAtUtc;
			if (age < TimeSpan.Zero)
				return AutoFarmSystemCacheDecision.Refresh;

			if (snapshot.IsEmpty && age < emptySystemCooldown)
				return AutoFarmSystemCacheDecision.Skip;

			return age < dataTtl
				? AutoFarmSystemCacheDecision.UseSnapshot
				: AutoFarmSystemCacheDecision.Refresh;
		}
	}
}
