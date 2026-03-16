using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBonuses {

		public LFBonusesResources LfResourceBonuses { get; set; }
		public LFBonusesCharacterClasses CharacterClassesBonuses { get; set; }
		public Dictionary<string, LFBonusesShip> LfShipBonuses { get; set; }
		public Dictionary<string, LFBonusesBase> CostTimeBonuses { get; set; }
		public LFBonusesMisc MiscBonuses { get; set; }
		public Dictionary<int, LFBonusesShip> LfShipBonusesInt {
			get {
				if (LfShipBonuses == null)
					return new Dictionary<int, LFBonusesShip>();
				return LfShipBonuses
					.Where(kvp => int.TryParse(kvp.Key, out _))
					.ToDictionary(kvp => int.Parse(kvp.Key), kvp => kvp.Value);
			}
        }
		public Dictionary<int, LFBonusesBase> CostTimeBonusesInt {
			get {
				if (CostTimeBonuses == null)
					return new Dictionary<int, LFBonusesBase>();
				return CostTimeBonuses
					.Where(kvp => int.TryParse(kvp.Key, out _))
					.ToDictionary(kvp => int.Parse(kvp.Key), kvp => kvp.Value);
			}
        }


		public LFBonusesProduction Production { get; set; }
		public LFBonusesExpeditions Expeditions { get; set; }
		public LFBonusesDens Dens { get; set; }
		public LFBonusesMoons Moons { get; set; }
		public LFBonusesCrawlers Crawlers { get; set; }
		public Dictionary<int, LFBonusesShip> Ships { get; set; }
		public Dictionary<int, LFBonusesShip> Defenses { get; set; }
		public Dictionary<int, LFBonusesBase> Buildings { get; set; }
		public Dictionary<int, LFBonusesBase> Researches { get; set; }
		public Dictionary<int, LFBonusesBase> LfBuildings { get; set; }
		public Dictionary<int, LFBonusesBase> LfResearches { get; set; }

		public float PhalanxRange { get; set; }
		public float RecallRefund { get; set; }
		public float FleetSlots { get; set; }
		public float Explorations { get; set; }
		public float SpaceDock { get; set; }
		public float PlanetSize { get; set; }
		public float InactivesLoot { get; set; }

		public LFBonuses() {
			LfResourceBonuses = new LFBonusesResources();
			CharacterClassesBonuses = new LFBonusesCharacterClasses();
			LfShipBonuses = new Dictionary<string, LFBonusesShip>();
			CostTimeBonuses = new Dictionary<string, LFBonusesBase>();
			MiscBonuses = new LFBonusesMisc();

			Production = new LFBonusesProduction();
			Expeditions = new LFBonusesExpeditions();
			Dens = new LFBonusesDens();
			Moons = new LFBonusesMoons();
			Crawlers = new LFBonusesCrawlers();
			Ships = new Dictionary<int, LFBonusesShip>();
			Defenses = new Dictionary<int, LFBonusesShip>();
			Buildings = new Dictionary<int, LFBonusesBase>();
			Researches = new Dictionary<int, LFBonusesBase>();
			LfBuildings = new Dictionary<int, LFBonusesBase>();
			LfResearches = new Dictionary<int, LFBonusesBase>();
		}

		public float GetShipCargoBonus(Buildables buildable) {
			float bonusCargo = 0;
			if (this != null && LfShipBonusesInt != null && LfShipBonusesInt.Count > 0 && LfShipBonusesInt.ContainsKey((int) buildable)) {
				bonusCargo = LfShipBonusesInt.GetValueOrDefault((int) buildable).CargoCapacity;
			}
			return bonusCargo;
		}

		public float GetShipSpeedBonus(Buildables buildable) {
			float bonusSpeed = 0;
			if (this != null && LfShipBonusesInt != null && LfShipBonusesInt.Count > 0 && LfShipBonusesInt.ContainsKey((int) buildable)) {
				bonusSpeed = LfShipBonusesInt.GetValueOrDefault((int) buildable).Speed;
			}
			return bonusSpeed;
		}

		public float GetShipConsumptionBonus(Buildables buildable) {
			float bonusCons = 0;
			if (this != null && LfShipBonusesInt != null && LfShipBonusesInt.Count > 0 && LfShipBonusesInt.ContainsKey((int) buildable)) {
				bonusCons = LfShipBonusesInt.GetValueOrDefault((int) buildable).FuelConsumption;
			}
			return bonusCons;
		}
	}
}
