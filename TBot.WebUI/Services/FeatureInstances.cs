using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tbot.Common.Settings;

namespace TBot.WebUI.Services {
	public static class FeatureInstances {
		public static async Task<List<string>> GetInstanceAliases() {
			var aliases = new List<string>();
			var settingsPath = SettingsService.GlobalSettingsPath;
			if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath)) {
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
