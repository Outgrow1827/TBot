using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFTechs {
		//Humans
		public int IntergalacticEnvoys { get; set; }
		public int HighPerformanceExtractors { get; set; }
		public int FusionDrives { get; set; }
		public int StealthFieldGenerator { get; set; }
		public int OrbitalDen { get; set; }
		public int ResearchAI { get; set; }
		public int HighPerformanceTerraformer { get; set; }
		public int EnhancedProductionTechnologies { get; set; }
		public int LightFighterMkII { get; set; }
		public int CruiserMkII { get; set; }
		public int ImprovedLabTechnology { get; set; }
		public int PlasmaTerraformer { get; set; }
		public int LowTemperatureDrives { get; set; }
		public int BomberMkII { get; set; }
		public int DestroyerMkII { get; set; }
		public int BattlecruiserMkII { get; set; }
		public int RobotAssistants { get; set; }
		public int Supercomputer { get; set; }

		//Rocktal
		public int VolcanicBatteries { get; set; }
		public int AcousticScanning { get; set; }
		public int HighEnergyPumpSystems { get; set; }
		public int CargoHoldExpansionCivilianShips { get; set; }
		public int MagmaPoweredProduction { get; set; }
		public int GeothermalPowerPlants { get; set; }
		public int DepthSounding { get; set; }
		public int IonCrystalEnhancementHeavyFighter { get; set; }
		public int ImprovedStellarator { get; set; }
		public int HardenedDiamondDrillHeads { get; set; }
		public int SeismicMiningTechnology { get; set; }
		public int MagmaPoweredPumpSystems { get; set; }
		public int IonCrystalModules { get; set; }
		public int OptimisedSiloConstructionMethod { get; set; }
		public int DiamondEnergyTransmitter { get; set; }
		public int ObsidianShieldReinforcement { get; set; }
		public int RuneShields { get; set; }
		public int RocktalCollectorEnhancement { get; set; }

		//Mechas
		public int CatalyserTechnology { get; set; }
		public int PlasmaDrive { get; set; }
		public int EfficiencyModule { get; set; }
		public int DepotAI { get; set; }
		public int GeneralOverhaulLightFighter { get; set; }
		public int AutomatedTransportLines { get; set; }
		public int ImprovedDroneAI { get; set; }
		public int ExperimentalRecyclingTechnology { get; set; }
		public int GeneralOverhaulCruiser { get; set; }
		public int SlingshotAutopilot { get; set; }
		public int HighTemperatureSuperconductors { get; set; }
		public int GeneralOverhaulBattleship { get; set; }
		public int ArtificialSwarmIntelligence { get; set; }
		public int GeneralOverhaulBattlecruiser { get; set; }
		public int GeneralOverhaulBomber { get; set; }
		public int GeneralOverhaulDestroyer { get; set; }
		public int ExperimentalWeaponsTechnology { get; set; }
		public int MechanGeneralEnhancement { get; set; }

		//Kaelesh
		public int HeatRecovery { get; set; }
		public int SulphideProcess { get; set; }
		public int PsionicNetwork { get; set; }
		public int TelekineticTractorBeam { get; set; }
		public int EnhancedSensorTechnology { get; set; }
		public int NeuromodalCompressor { get; set; }
		public int NeuroInterface { get; set; }
		public int InterplanetaryAnalysisNetwork { get; set; }
		public int OverclockingHeavyFighter { get; set; }
		public int TelekineticDrive { get; set; }
		public int SixthSense { get; set; }
		public int Psychoharmoniser { get; set; }
		public int EfficientSwarmIntelligence { get; set; }
		public int OverclockingLargeCargo { get; set; }
		public int GravitationSensors { get; set; }
		public int OverclockingBattleship { get; set; }
		public int PsionicShieldMatrix { get; set; }
		public int KaeleshDiscovererEnhancement { get; set; }

		// Built once from reflection at class-init time instead of on every GetLevel()/SetLevel() call - see
		// Ships.cs for the same pattern applied to the (much hotter) ship-count accessors.
		private static readonly Dictionary<LFTechno, (Func<LFTechs, int> Get, Action<LFTechs, int> Set)> _accessors = BuildAccessors();

		private static Dictionary<LFTechno, (Func<LFTechs, int>, Action<LFTechs, int>)> BuildAccessors() {
			var map = new Dictionary<LFTechno, (Func<LFTechs, int>, Action<LFTechs, int>)>();
			foreach (PropertyInfo prop in typeof(LFTechs).GetProperties()) {
				if (prop.PropertyType != typeof(int) || !Enum.TryParse<LFTechno>(prop.Name, out var building))
					continue;

				var instance = Expression.Parameter(typeof(LFTechs), "instance");
				var getter = Expression.Lambda<Func<LFTechs, int>>(Expression.Property(instance, prop), instance).Compile();

				var value = Expression.Parameter(typeof(int), "value");
				var setter = Expression.Lambda<Action<LFTechs, int>>(Expression.Assign(Expression.Property(instance, prop), value), instance, value).Compile();

				map[building] = (getter, setter);
			}
			return map;
		}

		public int GetLevel(LFTechno building) {
			return _accessors.TryGetValue(building, out var accessor) ? accessor.Get(this) : 0;
		}

		public LFTechs SetLevel(LFTechno buildable, int level) {
			if (_accessors.TryGetValue(buildable, out var accessor))
				accessor.Set(this, level);
			return this;
		}
	}

}
