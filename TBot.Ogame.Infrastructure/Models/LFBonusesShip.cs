using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBonusesShip {
		public float Armour { get; set; }
		public float Shield { get; set; }
        public float Weapon { get; set; }
        public float Cargo { get; set; }
        public float Speed { get; set; }
        public float Consumption { get; set; }
        public float Duration { get; set; }

		public LFBonusesShip(float armour = 0, float shield = 0, float weapon = 0, float cargo = 0, float speed = 0, float consumption = 0, float duration = 0) {
			Armour = armour;
			Shield = shield;
			Weapon = weapon;
			Cargo = cargo;
			Speed = speed;
			Consumption = consumption;
			Duration = duration;
		}
    }
}
