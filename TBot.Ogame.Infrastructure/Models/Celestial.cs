using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class Celestial {
		public int ID { get; set; }
		public string Img { get; set; }
		public string Name { get; set; }
		public int Diameter { get; set; }
		public int Activity { get; set; }
		public Coordinate Coordinate { get; set; }
		public Fields Fields { get; set; }
		public Resources Resources { get; set; }
		public Ships Ships { get; set; }
		public Defences Defences { get; set; }
		public Buildings Buildings { get; set; }
		public LFTypes LFtype { get; set; }
		public LFBuildings LFBuildings { get; set; }
		public LFTechs LFTechs { get; set; }
		public LFBonuses LFBonuses {get; set; }
		public Facilities Facilities { get; set; }
		public List<Production> Productions { get; set; }
		public Constructions Constructions { get; set; }
		public ResourceSettings ResourceSettings { get; set; }
		public ResourcesProduction ResourcesProduction { get; set; }
		public Debris Debris { get; set; }

		public override string ToString() {
			return $"{Name} {Coordinate.ToString()}";
		}

		public bool HasProduction() {
			try {
				return Productions.Count != 0;
			} catch {
				return false;
			}
		}

		public bool HasConstruction() {
			try {
				return Constructions.BuildingID != (int) Buildables.Null;
			} catch {
				return false;
			}
		}

		public bool HasCoords(Coordinate coords) {
			return coords.Galaxy == Coordinate.Galaxy
				&& coords.System == Coordinate.System
				&& coords.Position == Coordinate.Position
				&& coords.Type == Coordinate.Type;
		}

		// Delegates to Buildings/Facilities/LFBuildings/LFTechs' own compiled accessor dictionaries
		// (see Ships.cs for the pattern) instead of running GetType().GetProperties() directly here.
		public int GetLevel(Buildables building) {
			int output = Buildings.GetLevel(building);
			if (output == 0)
				output = Facilities.GetLevel(building);
			return output;
		}

		public Celestial SetLevel(Buildables building, int level) {
			Buildings.SetLevel(building, level);
			Facilities.SetLevel(building, level);
			return this;
		}

		public int GetLevel(LFBuildables building) {
			return LFBuildings.GetLevel(building);
		}

		public Celestial SetLevel(LFBuildables building, int level) {
			LFBuildings.SetLevel(building, level);
			return this;
		}

		public int GetLevel(LFTechno techno) {
			return LFTechs.GetLevel(techno);
		}

		public LFTypes SetLFType() {
			return (LFTypes) LFBuildings.LifeformType;
		}
	}

}
