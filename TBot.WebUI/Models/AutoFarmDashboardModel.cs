using System;
using System.Collections.Generic;

namespace TBot.WebUI.Models {
	public sealed class AutoFarmDashboardModel {
		public string SelectedInstance { get; init; }
		public IReadOnlyList<string> Instances { get; init; } = Array.Empty<string>();
		public bool DatabaseAvailable { get; init; }
		public int CachedSystemCount { get; init; }
		public DateTime? LastSystemObservedAtUtc { get; init; }
		public IReadOnlyList<AutoFarmReportRow> Reports { get; init; } = Array.Empty<AutoFarmReportRow>();
		public IReadOnlyList<AutoFarmAttackRow> Attacks { get; init; } = Array.Empty<AutoFarmAttackRow>();

		public int PendingAttackCount => Reports.Count(report => report.State == "AttackPending");
	}

	public sealed class AutoFarmReportRow {
		public string TargetName { get; init; }
		public string Coordinate { get; init; }
		public string State { get; init; }
		public int ReportId { get; init; }
		public DateTime? ReportDateUtc { get; init; }
		public DateTime? UpdatedAtUtc { get; init; }
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public int? ConsumedReportId { get; init; }
		public long TotalResources => Metal + Crystal + Deuterium;
	}

	public sealed class AutoFarmAttackRow {
		public string TargetName { get; init; }
		public string Coordinate { get; init; }
		public int ReportId { get; init; }
		public DateTime DispatchedAtUtc { get; init; }
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public long TotalResources => Metal + Crystal + Deuterium;
	}
}
