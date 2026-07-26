using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBonusesExpeditions {
		public float Ships { get; set; }
		public float Resources { get; set; }
        public float Speed { get; set; }
        public float DarkMatter { get; set; }
        public float FleetLoss { get; set; }
        public float Slots { get; set; }
        public float LessEnemies {get; set;}

		public LFBonusesExpeditions(float ships = 0, float resources = 0, float speed = 0, float darkmatter = 0, float fleetloss = 0, float slots = 0, float lessenemies = 0) {
			Ships = ships;
			Resources = resources;
			Speed = speed;
			DarkMatter = darkmatter;
			FleetLoss = fleetloss;
			Slots = slots;
			LessEnemies = lessenemies;
		}
	}
}
