using System;
using System.Collections.Generic;

namespace TBot.WebUI.Models {
	public sealed class AutoFarmDashboardModel {
		public string SelectedInstance { get; init; } = string.Empty;
		public IReadOnlyList<string> Instances { get; init; } = Array.Empty<string>();
		public bool DatabaseAvailable { get; init; }
		public int CachedSystemCount { get; init; }
		public DateTime? LastSystemObservedAtUtc { get; init; }
		public IReadOnlyList<AutoFarmReportRow> Reports { get; init; } = Array.Empty<AutoFarmReportRow>();
		public IReadOnlyList<AutoFarmAttackRow> Attacks { get; init; } = Array.Empty<AutoFarmAttackRow>();
		public AutoFarmWorkerStatus Worker { get; init; } = new();
		public AutoFarmSlotStatus Slots { get; init; } = new();
		public AutoFarmScanStatus Scan { get; init; } = new();
		public IReadOnlyList<AutoFarmSystemRow> Systems { get; init; } = Array.Empty<AutoFarmSystemRow>();
		// Full scan coverage map (every scanned system, not just the most recent 1000 like
		// Systems above) plus the configured ScanRange bounds - together enough to draw a
		// per-galaxy grid of which coordinates have been checked. Requested live 2026-09-02:
		// "queria saber quais galaxias, sistemas... foram escaneados... como um mapa".
		public IReadOnlyList<AutoFarmScanMapCell> ScanMap { get; init; } = Array.Empty<AutoFarmScanMapCell>();
		public IReadOnlyList<AutoFarmScanRangeInfo> ScanRanges { get; init; } = Array.Empty<AutoFarmScanRangeInfo>();
		public IReadOnlyList<AutoFarmLogRow> Logs { get; init; } = Array.Empty<AutoFarmLogRow>();
		public IReadOnlyList<AutoFarmLogRow> Errors { get; init; } = Array.Empty<AutoFarmLogRow>();
		public IReadOnlyList<AutoFarmCombatResultRow> CombatResults { get; init; } = Array.Empty<AutoFarmCombatResultRow>();
		public AutoFarmResultsTotal ResultsTotal { get; init; } = new();

		public int PendingAttackCount => Reports.Count(report => report.State == "AttackPending");
	}

	public sealed class AutoFarmWorkerStatus {
		public string State { get; init; } = "Unknown";
		public DateTime? LastExecutionUtc { get; init; }
		public DateTime? LastActivityUtc { get; init; }
		public DateTime? NextExecutionUtc { get; init; }
		public string NextExecutionText { get; init; } = "Unknown";
	}

	public sealed class AutoFarmSlotStatus {
		public int? MaxSlots { get; init; }
		public int? UsedSlots { get; init; }
		public int? AvailableSlots { get; init; }
		public int ProbesInFlight { get; init; }
		public int AttacksInFlight { get; init; }
	}

	public sealed class AutoFarmScanStatus {
		public int CursorGalaxy { get; init; }
		public int CursorSystem { get; init; }
		public int CachedSystems { get; init; }
		public int ScannedSystemsInRange { get; init; }
		public int TotalSystemsInRange { get; init; }
		public int ProgressPercent { get; init; }
		public DateTime? CursorUpdatedAtUtc { get; init; }
		public DateTime? LastSystemObservedAtUtc { get; init; }
	}

	public sealed class AutoFarmScanMapCell {
		public int Galaxy { get; init; }
		public int System { get; init; }
		public bool IsEmpty { get; init; }
	}

	public sealed class AutoFarmScanRangeInfo {
		public int Galaxy { get; init; }
		public int StartSystem { get; init; }
		public int EndSystem { get; init; }
	}

	public sealed class AutoFarmSystemRow {
		public string Coordinate { get; init; } = string.Empty;
		public string Status { get; init; } = string.Empty;
		public string Reason { get; init; } = string.Empty;
		public DateTime? LastScannedUtc { get; init; }
		public DateTime? NextRefreshUtc { get; init; }
	}

	// Populated from farm_results_{alias}.json (FeatureResultsStore), written by AutoFarmWorker's
	// LogResolvedCombatReports once a dispatched attack's real combat report becomes available.
	public sealed class AutoFarmCombatResultRow {
		public string Coordinate { get; init; } = string.Empty;
		public string Summary { get; init; } = string.Empty;
		public DateTime TimestampUtc { get; init; }
	}

	public sealed class AutoFarmReportRow {
		public string TargetName { get; init; } = string.Empty;
		public string Coordinate { get; init; } = string.Empty;
		public string State { get; init; } = string.Empty;
		public int ReportId { get; init; }
		public DateTime? ReportDateUtc { get; init; }
		public DateTime? UpdatedAtUtc { get; init; }
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public int? ConsumedReportId { get; init; }
		public string Reason { get; init; } = string.Empty;
		public string ReasonDetail { get; init; } = string.Empty;
		public long TotalResources => Metal + Crystal + Deuterium;
	}

	public sealed class AutoFarmAttackRow {
		public string TargetName { get; init; } = string.Empty;
		public string Coordinate { get; init; } = string.Empty;
		public string Origin { get; init; } = string.Empty;
		public string Mission { get; init; } = string.Empty;
		public int ReportId { get; init; }
		public DateTime DispatchedAtUtc { get; init; }
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public long TotalResources => Metal + Crystal + Deuterium;
	}

	public sealed class AutoFarmResultsTotal {
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
	}

	public sealed class AutoFarmLogRow {
		public string Level { get; init; } = string.Empty;
		public string Sender { get; init; } = string.Empty;
		public DateTime? DateTimeUtc { get; init; }
		public string Message { get; init; } = string.Empty;
		public string CopyText { get; init; } = string.Empty;
	}
}
