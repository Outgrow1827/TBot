using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;
using TBot.WebUI.Services;

namespace TBot.WebUI.Controllers {
	public sealed class AutoDiscoveryController : Controller {
		private static readonly FeatureDashboardReader Reader = new();

		// ResultsFileName depends on the instance alias (matches ExpeditionsWorker's
		// FeatureResultsStore file name, "discovery_results_{InstanceAlias}.json" - discovery
		// results are written by ExpeditionsWorker, since they share the same in-game message tab
		// as expeditions), so the config is built per request.
		private static FeatureDashboardConfig BuildConfig(string instanceAlias) => new() {
			Sender = "AutoDiscovery",
			NextCheckMarkers = new[] { "Next AutoDiscovery check at " },
			DispatchMarker = "Discovery fleet sent to",
			MaxSlotsSettingSection = "AutoDiscovery",
			LiveFleetsMission = Missions.Discovery,
			ResultsFileName = $"discovery_results_{instanceAlias}.json",
			ResultsShowArtifactsColumn = true
		};

		[HttpGet]
		public async Task<IActionResult> Index(string instance) {
			var instances = await FeatureInstances.GetInstanceAliases();
			return View("~/Views/Shared/FeatureDashboard.cshtml", await BuildModel(instances, instance));
		}

		[HttpGet]
		[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
		public async Task<IActionResult> Snapshot(string instance) {
			var instances = await FeatureInstances.GetInstanceAliases();
			return Json(await BuildModel(instances, instance));
		}

		private static async Task<FeatureDashboardModel> BuildModel(IReadOnlyList<string> instances, string instance) {
			var selected = instances.FirstOrDefault(alias => string.Equals(alias, instance, StringComparison.OrdinalIgnoreCase))
				?? instances.First();
			var model = await Reader.Read(BuildConfig(selected), selected,
				"AutoDiscovery",
				"Read-only diagnostic information about discovery fleets sent by TBot.");
			return new FeatureDashboardModel {
				FeatureTitle = model.FeatureTitle,
				FeatureIntro = model.FeatureIntro,
				SelectedInstance = selected,
				Instances = instances,
				Worker = model.Worker,
				DispatchesToday = model.DispatchesToday,
				MaxSlots = model.MaxSlots,
				Logs = model.Logs,
				Errors = model.Errors,
				LiveFleets = model.LiveFleets,
				LiveFleetsAvailable = model.LiveFleetsAvailable,
				Results = model.Results,
				ResultsTotal = model.ResultsTotal,
				ResultsShowArtifactsColumn = model.ResultsShowArtifactsColumn
			};
		}
	}
}
