using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBuildings {
		public int LifeformType { get; set; }

		//humans
		public int ResidentialSector { get; set; }
		public int BiosphereFarm { get; set; }
		public int ResearchCentre { get; set; }
		public int AcademyOfSciences { get; set; }
		public int NeuroCalibrationCentre { get; set; }
		public int HighEnergySmelting { get; set; }
		public int FoodSilo { get; set; }
		public int FusionPoweredProduction { get; set; }
		public int Skyscraper { get; set; }
		public int BiotechLab { get; set; }
		public int Metropolis { get; set; }
		public int PlanetaryShield { get; set; }

		//Rocktal
		public int MeditationEnclave { get; set; }
		public int CrystalFarm { get; set; }
		public int RuneTechnologium { get; set; }
		public int RuneForge { get; set; }
		public int Oriktorium { get; set; }
		public int MagmaForge { get; set; }
		public int DisruptionChamber { get; set; }
		public int Megalith { get; set; }
		public int CrystalRefinery { get; set; }
		public int DeuteriumSynthesiser { get; set; }
		public int MineralResearchCentre { get; set; }
		public int AdvancedRecyclingPlant { get; set; }

		//Mechas
		public int AssemblyLine { get; set; }
		public int FusionCellFactory { get; set; }
		public int RoboticsResearchCentre { get; set; }
		public int UpdateNetwork { get; set; }
		public int QuantumComputerCentre { get; set; }
		public int AutomatisedAssemblyCentre { get; set; }
		public int HighPerformanceTransformer { get; set; }
		public int MicrochipAssemblyLine { get; set; }
		public int ProductionAssemblyHall { get; set; }
		public int HighPerformanceSynthesiser { get; set; }
		public int ChipMassProduction { get; set; }
		public int NanoRepairBots { get; set; }

		//Kaelesh
		public int Sanctuary { get; set; }
		public int AntimatterCondenser { get; set; }
		public int VortexChamber { get; set; }
		public int HallsOfRealisation { get; set; }
		public int ForumOfTranscendence { get; set; }
		public int AntimatterConvector { get; set; }
		public int CloningLaboratory { get; set; }
		public int ChrysalisAccelerator { get; set; }
		public int BioModifier { get; set; }
		public int PsionicModulator { get; set; }
		public int ShipManufacturingHall { get; set; }
		public int SupraRefractor { get; set; }

		// Built once via reflection instead of on every GetLevel()/SetLevel() call.
		private static readonly Dictionary<LFBuildables, (Func<LFBuildings, int> Get, Action<LFBuildings, int> Set)> _accessors = BuildAccessors();

		private static Dictionary<LFBuildables, (Func<LFBuildings, int>, Action<LFBuildings, int>)> BuildAccessors() {
			var map = new Dictionary<LFBuildables, (Func<LFBuildings, int>, Action<LFBuildings, int>)>();
			foreach (PropertyInfo prop in typeof(LFBuildings).GetProperties()) {
				if (prop.PropertyType != typeof(int) || !Enum.TryParse<LFBuildables>(prop.Name, out var building))
					continue;

				var instance = Expression.Parameter(typeof(LFBuildings), "instance");
				var getter = Expression.Lambda<Func<LFBuildings, int>>(Expression.Property(instance, prop), instance).Compile();

				var value = Expression.Parameter(typeof(int), "value");
				var setter = Expression.Lambda<Action<LFBuildings, int>>(Expression.Assign(Expression.Property(instance, prop), value), instance, value).Compile();

				map[building] = (getter, setter);
			}
			return map;
		}

		public int GetLevel(LFBuildables building) {
			return _accessors.TryGetValue(building, out var accessor) ? accessor.Get(this) : 0;
		}

		public LFBuildings SetLevel(LFBuildables buildable, int level) {
			if (_accessors.TryGetValue(buildable, out var accessor))
				accessor.Set(this, level);
			return this;
		}
	}

}
