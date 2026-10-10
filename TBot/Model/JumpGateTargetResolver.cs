using System;
using System.Collections.Generic;

namespace TBot.Model {
	public readonly record struct JumpGateTarget(int Galaxy, int System, int Position);

	/// <summary>
	/// Reads the AutoFleetJumpGate.Target setting, which accepts either a single target object
	/// (kept for backwards compatibility) or an array of targets. The array lets the worker fall
	/// back to the next moon in the list when the current one has no JumpGate facility (built,
	/// destroyed, or never had one) - resolving to a single JumpGateTarget would silently drop
	/// every entry but the first.
	/// </summary>
	public static class JumpGateTargetResolver {
		public static List<JumpGateTarget> ResolveAll(IDictionary<string, object> settings) {
			if (settings == null || !settings.TryGetValue("Target", out var rawTarget) || rawTarget == null)
				throw new InvalidOperationException("Brain.AutoFleetJumpGate.Target settings are missing.");

			var results = new List<JumpGateTarget>();
			if (rawTarget is Array targetArray) {
				if (targetArray.Length == 0)
					throw new InvalidOperationException("Brain.AutoFleetJumpGate.Target array is empty.");

				foreach (var item in targetArray) {
					if (item is IDictionary<string, object> entry)
						results.Add(ToTarget(entry));
				}
			} else if (rawTarget is IDictionary<string, object> target) {
				results.Add(ToTarget(target));
			} else {
				throw new InvalidOperationException("Brain.AutoFleetJumpGate.Target must be an object or an array of objects.");
			}

			if (results.Count == 0)
				throw new InvalidOperationException("Brain.AutoFleetJumpGate.Target did not contain any valid entry.");

			return results;
		}

		// Kept for callers that only ever want the first configured target.
		public static JumpGateTarget Resolve(IDictionary<string, object> settings, out bool usedLegacyArray) {
			usedLegacyArray = settings != null && settings.TryGetValue("Target", out var rawTarget) && rawTarget is Array;
			var all = ResolveAll(settings);
			return all[0];
		}

		private static JumpGateTarget ToTarget(IDictionary<string, object> target) {
			return new JumpGateTarget(
				Convert.ToInt32(target["Galaxy"]),
				Convert.ToInt32(target["System"]),
				Convert.ToInt32(target["Position"]));
		}
	}
}
