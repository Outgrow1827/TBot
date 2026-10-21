using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using TBot.Ogame.Infrastructure;

namespace Tbot.Workers {
	// In-memory cache of the public players list, keyed by player ID, exposing the raw status string, which tells a short inactivity apart from a long one where the galaxy scan's flag cannot.
	public class PlayerStatusCache {
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(60);

		private readonly IOgameService _ogameService;
		private Dictionary<int, string> _statusById = new();
		// The id->name map survives renames, so callers can reconcile a known id against its current name instead of matching on the name.
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
