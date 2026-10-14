using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using TBot.Ogame.Infrastructure;

namespace Tbot.Workers {
	// In-memory cache of OGame's public /api/players.xml, keyed by player ID. Exposes each player's
	// raw status string (a/v/i/I/b/o, combinable eg. "vI") - a galaxy scan only ever gives a single
	// collapsed Inactive flag (Planet.Inactive), which can't tell a short inactivity (i, ~7+ days)
	// apart from a long one (I, ~28+ days). Refetched at most once per RefreshInterval.
	public class PlayerStatusCache {
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(60);

		private readonly IOgameService _ogameService;
		private Dictionary<int, string> _statusById = new();
		// Player names change in-game; anything keying history by name alone silently splits into
		// "two different players" for the same real account. players.xml's id is stable across a
		// rename, so retaining id->name here lets callers reconcile a known id against its latest
		// name instead of relying on name matching.
		private Dictionary<int, string> _nameById = new();
		private DateTime _lastRefresh = DateTime.MinValue;

		public PlayerStatusCache(IOgameService ogameService) {
			_ogameService = ogameService;
		}

		// Null means "unknown" (player not found, or fetch failed) - callers should fall back to
		// their own conservative default rather than assume any particular status.
		public async Task<string> GetStatus(int playerId) {
			if (playerId <= 0)
				return null;
			await EnsureFresh();
			return _statusById.TryGetValue(playerId, out var status) ? status : null;
		}

		// Null means "unknown" (player not found, or fetch failed).
		public async Task<string> GetName(int playerId) {
			if (playerId <= 0)
				return null;
			await EnsureFresh();
			return _nameById.TryGetValue(playerId, out var name) ? name : null;
		}

		private async Task EnsureFresh() {
			if (DateTime.UtcNow - _lastRefresh < RefreshInterval && _statusById.Count > 0)
				return;
			try {
				var xml = await _ogameService.GetPlayersXml();
				var doc = XDocument.Parse(xml);
				var players = doc.Root?.Descendants("player")
					.Where(p => p.Attribute("id") != null)
					.GroupBy(p => int.Parse(p.Attribute("id")!.Value))
					.Select(g => g.First())
					.ToList() ?? new List<XElement>();
				_statusById = players.ToDictionary(p => int.Parse(p.Attribute("id")!.Value), p => p.Attribute("status")?.Value ?? "");
				_nameById = players.ToDictionary(p => int.Parse(p.Attribute("id")!.Value), p => p.Attribute("name")?.Value ?? "");
				_lastRefresh = DateTime.UtcNow;
			} catch {
				// Keep serving the previous snapshot (if any) rather than throwing - this is a
				// best-effort pre-filter, not a required data source.
			}
		}
	}
}
