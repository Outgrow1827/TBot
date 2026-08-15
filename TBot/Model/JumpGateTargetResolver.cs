using System;
using System.Collections.Generic;

namespace TBot.Model {
	public readonly record struct JumpGateTarget(int Galaxy, int System, int Position);

	/// <summary>
	/// Reads the current JumpGate target shape and the legacy single-item array
	/// shape. The worker can therefore migrate the default without breaking an
	/// existing instance file.
	/// </summary>
	public static class JumpGateTargetResolver {
		public static JumpGateTarget Resolve(
			IDictionary<string, object> settings,
			out bool usedLegacyArray) {
			if (settings == null || !settings.TryGetValue("Target", out var rawTarget) || rawTarget == null)
				throw new InvalidOperationException("Brain.AutoFleetJumpGate.Target settings are missing.");

			usedLegacyArray = false;
			if (rawTarget is Array targetArray) {
				if (targetArray.Length == 0)
					throw new InvalidOperationException("Brain.AutoFleetJumpGate.Target array is empty.");

				usedLegacyArray = true;
				rawTarget = targetArray.GetValue(0);
			}

			if (!(rawTarget is IDictionary<string, object> target))
				throw new InvalidOperationException("Brain.AutoFleetJumpGate.Target must be an object.");

			return new JumpGateTarget(
				Convert.ToInt32(target["Galaxy"]),
				Convert.ToInt32(target["System"]),
				Convert.ToInt32(target["Position"]));
		}
	}
}
