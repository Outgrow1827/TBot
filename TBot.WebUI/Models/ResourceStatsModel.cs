using System;
using System.Collections.Generic;

namespace TBot.WebUI.Models {
	public sealed class ResourceStatsModel {
		public string SelectedInstance { get; init; } = string.Empty;
		public IReadOnlyList<string> Instances { get; init; } = Array.Empty<string>();
		public bool DatabaseAvailable { get; init; }
		public ResourceStatsTotals Totals { get; init; } = new();
		public IReadOnlyList<ResourceStatsCelestialRow> Celestials { get; init; } = Array.Empty<ResourceStatsCelestialRow>();
		public IReadOnlyList<ResourceStatsHistoryPoint> History { get; init; } = Array.Empty<ResourceStatsHistoryPoint>();
	}

	// Latest known hourly production/consumption and stockpile, summed across every celestial.
	public sealed class ResourceStatsTotals {
		public long MetalHourlyProduction { get; init; }
		public long CrystalHourlyProduction { get; init; }
		public long DeuteriumHourlyProduction { get; init; }
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public DateTime? LastUpdatedUtc { get; init; }
	}

	// One row per celestial, latest recorded snapshot only.
	public sealed class ResourceStatsCelestialRow {
		public string CelestialName { get; init; } = string.Empty;
		public string Coordinate { get; init; } = string.Empty;
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public long MetalHourlyProduction { get; init; }
		public long CrystalHourlyProduction { get; init; }
		public long DeuteriumHourlyProduction { get; init; }
		public long Energy { get; init; }
		public DateTime RecordedAtUtc { get; init; }
	}

	// One point per recorded snapshot cycle, summed across all celestials - feeds the history chart.
	public sealed class ResourceStatsHistoryPoint {
		public DateTime RecordedAtUtc { get; init; }
		public long Metal { get; init; }
		public long Crystal { get; init; }
		public long Deuterium { get; init; }
		public long MetalHourlyProduction { get; init; }
		public long CrystalHourlyProduction { get; init; }
		public long DeuteriumHourlyProduction { get; init; }
	}
}
