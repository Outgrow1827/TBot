using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBonusesShip {
		public int ID { get; set; }
		public float StructuralIntegrity { get; set; }
		public float ShieldPower { get; set; }
		public float WeaponPower { get; set; }
		public float Speed { get; set; }
		public float CargoCapacity { get; set; }
		public float FuelConsumption { get; set; }

		public LFBonusesShip(int id = -1, float armour = 0, float shield = 0, float weapon = 0, float speed = 0, float cargo = 0, float consumption = 0) {
			ID = id;
			StructuralIntegrity = armour;
			ShieldPower = shield;
			WeaponPower = weapon;
			Speed = speed;
			CargoCapacity = cargo;
			FuelConsumption = consumption;
		}
    }
}
