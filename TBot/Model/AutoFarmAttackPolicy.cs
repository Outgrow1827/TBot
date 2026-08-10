using System;
using System.Collections.Generic;
using System.Linq;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public static class AutoFarmAttackPolicy {
		public static bool IsAttackInProgress(FarmTarget target, IEnumerable<Fleet> fleets) {
			var destination = target?.Celestial?.Coordinate;
			if (destination == null)
				return false;

			return (fleets ?? Array.Empty<Fleet>())
				.Any(fleet => fleet != null &&
					fleet.Mission == Missions.Attack &&
					fleet.Destination?.IsSame(destination) == true);
		}

		public static bool CanUseReport(FarmTarget target, EspionageReport report) {
			if (target == null || report == null)
				return false;

			return !target.ConsumedReportId.HasValue ||
				target.ConsumedReportId.Value != report.ID;
		}

		public static bool CanProcessReport(FarmTarget target, EspionageReport report) {
			if (target == null || report == null || report.ID <= 0)
				return false;

			if (target.State == FarmState.AttackSent)
				return false;

			return CanUseReport(target, report);
		}
	}
}
