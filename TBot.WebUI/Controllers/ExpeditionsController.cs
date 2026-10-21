using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Tbot.Common.Settings;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;
using TBot.WebUI.Services;

namespace TBot.WebUI.Controllers {
	public sealed class ExpeditionsController : Controller {
		private static readonly FeatureDashboardReader Reader = new();

		// The results file name is per instance alias, so the config is built per request.
		private static FeatureDashboardConfig BuildConfig(string instanceAlias) => new() {
			Sender = "Expeditions",
			NextCheckMarkers = new[] { "Next expedition check at " },
			DispatchMarker = "Sending expedition from",
			// No MaxSlots setting for Expeditions: the cap is the account's live expedition pool (ExpTotal).
			LiveMaxSlotsFromExpeditionPool = true,
			LiveFleetsMission = Missions.Expedition,
			ResultsFileName = $"expedition_results_{instanceAlias}.json"
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
				"Expeditions",
				"Read-only diagnostic information about expedition fleets sent by TBot.");
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
