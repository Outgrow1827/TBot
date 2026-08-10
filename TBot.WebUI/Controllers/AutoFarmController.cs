using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Tbot.Common.Settings;
using TBot.WebUI.Models;
using TBot.WebUI.Services;

namespace TBot.WebUI.Controllers {
	public sealed class AutoFarmController : Controller {
		private readonly AutoFarmDashboardReader _reader = new();

		[HttpGet]
		public async Task<IActionResult> Index(string instance) {
			var instances = await GetInstanceAliases();
			var selected = instances.FirstOrDefault(alias => string.Equals(alias, instance, StringComparison.OrdinalIgnoreCase))
				?? instances.First();
			var storage = _reader.Read(selected);

			return View(new AutoFarmDashboardModel {
				SelectedInstance = selected,
				Instances = instances,
				DatabaseAvailable = storage.DatabaseAvailable,
				CachedSystemCount = storage.CachedSystemCount,
				LastSystemObservedAtUtc = storage.LastSystemObservedAtUtc,
				Reports = storage.Reports,
				Attacks = storage.Attacks
			});
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
