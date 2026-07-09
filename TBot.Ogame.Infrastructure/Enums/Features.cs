using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Enums {
	public static class Features {
		public static readonly List<Feature> AllFeatures = new List<Feature>() {
			Feature.Defender,
			Feature.BrainAutobuildCargo,
			Feature.BrainAutoRepatriate,
			Feature.BrainAutoMine,
			Feature.BrainLifeformAutoMine,
			Feature.BrainLifeformAutoResearch,
			Feature.BrainOfferOfTheDay,
			Feature.BrainAutoResearch,
			Feature.BrainAutoDefence,
			Feature.AutoFarm,
			Feature.Expeditions,
			Feature.AutoDiscovery,
			Feature.Colonize,
			Feature.Harvest,
			Feature.SleepMode,
			Feature.Watchdog,
			// ManualActivityLog is no longer a separate worker/feature - its detection logic runs as
			// part of AutoFarmWorker's own cycle (see AutoFarmWorker.IsManualActivityLogEnabled), to
			// avoid two independent pollers hitting ogamed on overlapping schedules.
		};
	}
}
