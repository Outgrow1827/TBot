using System;
using System.Collections.Generic;

namespace TBot.Ogame.Infrastructure.Models {
	/// <summary>
	/// Persistent record of a player encountered by AutoFarm (as a farm target) and/or Defender (as an
	/// attacker). Separate from FarmTargetCacheEntry (which is keyed by coordinate and only cares about
	/// the current celestial) - this is keyed by player and survives across all of that player's planets.
	/// </summary>
	public class PlayerRecord {
		public int PlayerID { get; set; }
		public string PlayerName { get; set; }
		public string LastKnownCoordinate { get; set; }
		public DateTime FirstSeen { get; set; }
		public DateTime LastSeen { get; set; }

		/// Every distinct coordinate (as ToString()) this player has ever been sighted at - attacking us,
		/// spying us, or farmed by us. Not a full picture of their empire (we only know what we've directly
		/// observed - there's no "list all of a player's planets" capability in the ogamed fork today), but
		/// lets counter-espionage (DefenderWorker) cover every planet/moon we already happen to know about
		/// instead of just the single most recent origin.
		public List<string> KnownCoordinates { get; set; } = new();

		public int TimesFarmedByUs { get; set; }
		public DateTime? LastFarmedDate { get; set; }
		public long TotalConfirmedLoot { get; set; }

		public bool RetaliatedAgainstUs { get; set; }
		public int RetaliationCount { get; set; }
		public DateTime? LastRetaliationDate { get; set; }
	}
}
