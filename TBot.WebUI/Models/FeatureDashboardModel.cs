using System;
using System.Collections.Generic;

namespace TBot.WebUI.Models {
	public sealed class FeatureDashboardModel {
		public string FeatureTitle { get; init; } = string.Empty;
		public string FeatureIntro { get; init; } = string.Empty;
		public string SelectedInstance { get; init; } = string.Empty;
		public IReadOnlyList<string> Instances { get; init; } = Array.Empty<string>();
		public FeatureWorkerStatus Worker { get; init; } = new();
		public int DispatchesToday { get; init; }
		public int? MaxSlots { get; init; }
		public IReadOnlyList<FeatureLogRow> Logs { get; init; } = Array.Empty<FeatureLogRow>();
		public IReadOnlyList<FeatureLogRow> Errors { get; init; } = Array.Empty<FeatureLogRow>();
		// Read live from ogamed's own /bot/fleets, independent of the worker/Active flag entirely -
		// shows real in-flight fleets for this mission whether they were sent by the bot or by hand.
		public IReadOnlyList<LiveFleetRow> LiveFleets { get; init; } = Array.Empty<LiveFleetRow>();
		public bool LiveFleetsAvailable { get; init; }
		// Structured results (data/*.json, written by FeatureResultsStore on the TBot side) -
		// replaces dumping each result's full narrative text into the worker log/CSV.
		public IReadOnlyList<FeatureResultRow> Results { get; init; } = Array.Empty<FeatureResultRow>();
		// Grand total across every stored result entry (not per-day) - requested live 2026-08-29
		// after the user noticed resources gained from expeditions/discoveries were never summed
		// anywhere, only shown per-message in the Results table above.
		public FeatureResultsTotal ResultsTotal { get; init; } = new();
		// AutoDiscovery results never carry Resources/Ships (see FeatureResultEntry.ArtifactsOrLifeform) -
		// when true, the Results table shows an "Artifacts/Lifeform" column instead of those two
		// always-empty ones.
		public bool ResultsShowArtifactsColumn { get; init; }
	}

	public sealed class FeatureResultRow {
		public int Id { get; init; }
		public string Coordinate { get; init; } = string.Empty;
		public string Summary { get; init; } = string.Empty;
		public string Resources { get; init; } = string.Empty;
		public string Ships { get; init; } = string.Empty;
		public DateTime TimestampUtc { get; init; }
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public long Darkmatter { get; init; }
		public string ArtifactsOrLifeform { get; init; } = string.Empty;
		public long ArtifactsFound { get; init; }
	}

	public sealed class FeatureResultsTotal {
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public long Darkmatter { get; init; }
		public long ArtifactsFound { get; init; }
		public long Total => Metal + Crystal + Deuterium + Darkmatter;
	}

	public sealed class LiveFleetRow {
		public string Mission { get; init; } = string.Empty;
		public string Origin { get; init; } = string.Empty;
		public string Destination { get; init; } = string.Empty;
		public bool ReturnFlight { get; init; }
		public DateTime? ArrivalTimeUtc { get; init; }
		public long TotalResources { get; init; }
	}

	public sealed class FeatureWorkerStatus {
		public string State { get; init; } = "Unknown";
		public DateTime? LastActivityUtc { get; init; }
		public DateTime? NextExecutionUtc { get; init; }
		public string NextExecutionText { get; init; } = "Unknown";
	}

	public sealed class FeatureLogRow {
		public string Level { get; init; } = string.Empty;
		public string Sender { get; init; } = string.Empty;
		public DateTime? DateTimeUtc { get; init; }
		public string Message { get; init; } = string.Empty;
		public string CopyText { get; init; } = string.Empty;
	}
}
