using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;
using TBot.WebUI.Services;

namespace TBot.WebUI.Controllers {
	public sealed class AutoHarvestController : Controller {
		private static readonly FeatureDashboardReader Reader = new();
		private static readonly FeatureDashboardConfig Config = new() {
			Sender = "Harvest",
			NextCheckMarkers = new[] { "Next check at " },
			DispatchMarker = "Harvesting debris in",
			MaxSlotsSettingSection = "AutoHarvest",
			LiveFleetsMission = Missions.Harvest
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
			var model = await Reader.Read(Config, selected,
				"AutoHarvest",
				"Read-only diagnostic information about debris fields harvested by TBot.");
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
				ResultsTotal = ReadHarvestLifetimeTotal(selected),
				ResultsShowArtifactsColumn = model.ResultsShowArtifactsColumn
			};
		}

		// AutoHarvest has no FeatureResultsStore of its own (its dispatch data lives in
		// TBotDataCache's "harvest_results" table, written by HarvestWorker.RecordHarvest) - read
		// the lifetime total directly from there instead, same data_{alias}.db file TBotDataCache
		// itself uses (see TBotDataCache.GetDatabaseFilePath).
		private static FeatureResultsTotal ReadHarvestLifetimeTotal(string instanceAlias) {
			try {
				var path = Path.Combine(AppContext.BaseDirectory, "data", $"data_{instanceAlias}.db");
				if (!System.IO.File.Exists(path))
					return new FeatureResultsTotal();

				using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
					DataSource = path,
					Pooling = false
				}.ToString());
				connection.Open();
				using var command = connection.CreateCommand();
				command.CommandText = @"
SELECT COALESCE(SUM(metal),0), COALESCE(SUM(crystal),0), COALESCE(SUM(deuterium),0)
FROM harvest_results;";
				using var reader = command.ExecuteReader();
				if (!reader.Read())
					return new FeatureResultsTotal();
				return new FeatureResultsTotal {
					Metal = reader.GetInt64(0),
					Crystal = reader.GetInt64(1),
					Deuterium = reader.GetInt64(2)
				};
			} catch {
				return new FeatureResultsTotal();
			}
		}
	}
}
