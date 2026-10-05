using System;

namespace TBot.Ogame.Infrastructure.Models {
	public class ExpeditionMessage {
		public int ID { get; set; }
		public Coordinate Coordinate { get; set; }
		public string Content { get; set; }
		public Resources Resources { get; set; }
		public Ships Ships { get; set; }
		public DateTime CreatedAt { get; set; }
		// 41 = normal expedition result, 61 = AutoDiscovery result - both share the same in-game
		// message tab (confirmed against OGLight TBot's tabID 22 "expeditions & discoveries").
		public long GlobalTypeID { get; set; }
		// Only populated for AutoDiscovery (GlobalTypeID 61) results with found artifacts.
		public string DiscoveryType { get; set; }
		public long ArtifactsFound { get; set; }
		public string ArtifactsSize { get; set; }
		// Only populated when DiscoveryType == "lifeform-xp".
		public string LifeformDiscovered { get; set; }
		public long LifeformGainedExperience { get; set; }
		public bool LifeformAlreadyOwned { get; set; }
	}
}
