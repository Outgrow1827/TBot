using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using TBot.Ogame.Infrastructure;

namespace Tbot.Workers {
	// In-memory cache of the public highscore list, keyed by player name (the only identifier for targets without their own Player object) and refetched at most once per refresh interval.
	public class HighscorePlayerRankCache {
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(60);

		private readonly IOgameService _ogameService;
		private Dictionary<string, int> _rankByName = new();
		private DateTime _lastRefresh = DateTime.MinValue;

		public HighscorePlayerRankCache(IOgameService ogameService) {
			_ogameService = ogameService;
		}

		public async Task<int?> GetRank(string playerName) {
			if (string.IsNullOrEmpty(playerName))
				return null;
			await EnsureFresh();
			return _rankByName.TryGetValue(playerName, out var rank) ? rank : null;
		}

		private async Task EnsureFresh() {
			if (DateTime.UtcNow - _lastRefresh < RefreshInterval && _rankByName.Count > 0)
				return;
			try {
				var xml = await _ogameService.GetHighscoreXml(category: 1, type: 0);
				var doc = XDocument.Parse(xml);
				_rankByName = doc.Root?.Elements("player")
					.Where(p => p.Attribute("name") != null && p.Attribute("position") != null)
					.GroupBy(p => p.Attribute("name")!.Value)
					.ToDictionary(g => g.Key, g => int.Parse(g.First().Attribute("position")!.Value))
					?? new Dictionary<string, int>();
				_lastRefresh = DateTime.UtcNow;
			} catch {
				// Keep serving the previous snapshot (if any) rather than throwing - this is a
				// best-effort pre-filter, not a required data source.
			}
		}
	}
}
