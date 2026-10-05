using System;
using System.Collections.Generic;

namespace TBot.Ogame.Infrastructure.Models {
	public class CombatReportSummary {
		public int ID { get; set; }
		public string APIKey { get; set; }
		public long FleetID { get; set; }
		public Coordinate Origin { get; set; }
		public Coordinate Destination { get; set; }
		public string AttackerName { get; set; }
		public string DefenderName { get; set; }
		public long Loot { get; set; }
		public long Metal { get; set; }
		public long Crystal { get; set; }
		public long Deuterium { get; set; }
		public long DebrisField { get; set; }
		public DateTime CreatedAt { get; set; }
		public Dictionary<string, long> LostShipsAttacker { get; set; } = new();
		public Dictionary<string, long> LostShipsDefender { get; set; } = new();
	}
}
