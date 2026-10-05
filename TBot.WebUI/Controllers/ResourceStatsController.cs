using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Tbot.Common.Settings;
using TBot.WebUI.Models;
using TBot.WebUI.Services;

namespace TBot.WebUI.Controllers {
	public sealed class ResourceStatsController : Controller {
		private readonly ResourceStatsReader _reader = new();

		[HttpGet]
		public async Task<IActionResult> Index(string instance) {
			var instances = await GetInstanceAliases();
			return View(BuildModel(instances, instance));
		}

		[HttpGet]
		[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
		public async Task<IActionResult> Snapshot(string instance) {
			var instances = await GetInstanceAliases();
			return Json(BuildModel(instances, instance));
		}

		private ResourceStatsModel BuildModel(IReadOnlyList<string> instances, string instance) {
			var selected = instances.FirstOrDefault(alias => string.Equals(alias, instance, StringComparison.OrdinalIgnoreCase))
				?? instances.First();
			var storage = _reader.Read(selected);

			return new ResourceStatsModel {
				SelectedInstance = selected,
				Instances = instances,
				DatabaseAvailable = storage.DatabaseAvailable,
				Totals = storage.Totals,
				Celestials = storage.Celestials,
				History = storage.History
			};
		}

		private static async Task<List<string>> GetInstanceAliases() {
			var aliases = new List<string>();
			var settingsPath = SettingsService.GlobalSettingsPath;
			if (!string.IsNullOrWhiteSpace(settingsPath) && System.IO.File.Exists(settingsPath)) {
				try {
					var settings = await SettingsService.GetSettings(settingsPath);
					if (SettingsService.IsSettingSet(settings, "Instances")) {
						foreach (var instance in settings.Instances) {
							var alias = (string) instance.Alias;
							if (!string.IsNullOrWhiteSpace(alias) && !aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
								aliases.Add(alias);
						}
					}
				} catch {
					// Keep the single-instance view available if settings are temporarily unreadable.
				}
			}

			if (!aliases.Any())
				aliases.Add("MAIN");
			return aliases;
		}
	}
}
