using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFTechs {
		//Humans
		public int? IntergalacticEnvoys { get; set; }
		public int? HighPerformanceExtractors { get; set; }
		public int? FusionDrives { get; set; }
		public int? StealthFieldGenerator { get; set; }
		public int? OrbitalDen { get; set; }
		public int? ResearchAI { get; set; }
		public int? HighPerformanceTerraformer { get; set; }
		public int? EnhancedProductionTechnologies { get; set; }
		public int? LightFighterMkII { get; set; }
		public int? CruiserMkII { get; set; }
		public int? ImprovedLabTechnology { get; set; }
		public int? PlasmaTerraformer { get; set; }
		public int? LowTemperatureDrives { get; set; }
		public int? BomberMkII { get; set; }
		public int? DestroyerMkII { get; set; }
		public int? BattlecruiserMkII { get; set; }
		public int? RobotAssistants { get; set; }
		public int? Supercomputer { get; set; }

		//Rocktal
		public int? VolcanicBatteries { get; set; }
		public int? AcousticScanning { get; set; }
		public int? HighEnergyPumpSystems { get; set; }
		public int? CargoHoldExpansionCivilianShips { get; set; }
		public int? MagmaPoweredProduction { get; set; }
		public int? GeothermalPowerPlants { get; set; }
		public int? DepthSounding { get; set; }
		public int? IonCrystalEnhancementHeavyFighter { get; set; }
		public int? ImprovedStellarator { get; set; }
		public int? HardenedDiamondDrillHeads { get; set; }
		public int? SeismicMiningTechnology { get; set; }
		public int? MagmaPoweredPumpSystems { get; set; }
		public int? IonCrystalModules { get; set; }
		public int? OptimisedSiloConstructionMethod { get; set; }
		public int? DiamondEnergyTransmitter { get; set; }
		public int? ObsidianShieldReinforcement { get; set; }
		public int? RuneShields { get; set; }
		public int? RocktalCollectorEnhancement { get; set; }

		//Mechas
		public int? CatalyserTechnology { get; set; }
		public int? PlasmaDrive { get; set; }
		public int? EfficiencyModule { get; set; }
		public int? DepotAI { get; set; }
		public int? GeneralOverhaulLightFighter { get; set; }
		public int? AutomatedTransportLines { get; set; }
		public int? ImprovedDroneAI { get; set; }
		public int? ExperimentalRecyclingTechnology { get; set; }
		public int? GeneralOverhaulCruiser { get; set; }
		public int? SlingshotAutopilot { get; set; }
		public int? HighTemperatureSuperconductors { get; set; }
		public int? GeneralOverhaulBattleship { get; set; }
		public int? ArtificialSwarmIntelligence { get; set; }
		public int? GeneralOverhaulBattlecruiser { get; set; }
		public int? GeneralOverhaulBomber { get; set; }
		public int? GeneralOverhaulDestroyer { get; set; }
		public int? ExperimentalWeaponsTechnology { get; set; }
		public int? MechanGeneralEnhancement { get; set; }

		//Kaelesh
		public int? HeatRecovery { get; set; }
		public int? SulphideProcess { get; set; }
		public int? PsionicNetwork { get; set; }
		public int? TelekineticTractorBeam { get; set; }
		public int? EnhancedSensorTechnology { get; set; }
		public int? NeuromodalCompressor { get; set; }
		public int? NeuroInterface { get; set; }
		public int? InterplanetaryAnalysisNetwork { get; set; }
		public int? OverclockingHeavyFighter { get; set; }
		public int? TelekineticDrive { get; set; }
		public int? SixthSense { get; set; }
		public int? Psychoharmoniser { get; set; }
		public int? EfficientSwarmIntelligence { get; set; }
		public int? OverclockingLargeCargo { get; set; }
		public int? GravitationSensors { get; set; }
		public int? OverclockingBattleship { get; set; }
		public int? PsionicShieldMatrix { get; set; }
		public int? KaeleshDiscovererEnhancement { get; set; }

		public LFTechs(int? intergalacticenvoys = null, int? highperformanceextractors = null, int? fusiondrives = null, int? stealthfieldgenerator = null, int? orbitalden = null, int? researchai = null, int? highperformanceterraformer = null, int? enhancedproductiontechnologies = null, int? lightfightermkii = null, int? cruisermkii = null, int? improvedlabtechnology = null, int? plasmaterraformer = null, int? lowtemperaturedrives = null, int? bombermkii = null, int? destroyermkii = null, int? battlecruisermkii = null, int? robotassistants = null, int? supercomputer = null, int? volcanicbatteries = null, int? acousticscanning = null, int? highenergypumpsystems = null, int? cargoholdexpansioncivilianships = null, int? magmapoweredproduction = null, int? geothermalpowerplants = null, int? depthsounding = null, int? ioncrystalenhancementheavyfighter = null, int? improvedstellarator = null, int? hardeneddiamonddrillheads = null, int? seismicminingtechnology = null, int? magmapoweredpumpsystems = null, int? ioncrystalmodules = null, int? optimisedsiloconstructionmethod = null, int? diamondenergytransmitter = null, int? obsidianshieldreinforcement = null, int? runeshields = null, int? rocktalcollectorenhancement = null, int? catalysertechnology = null, int? plasmadrive = null, int? efficiencymodule = null, int? depotai = null, int? generaloverhaullightfighter = null, int? automatedtransportlines = null, int? improveddroneai = null, int? experimentalrecyclingtechnology = null, int? generaloverhaulcruiser = null, int? slingshotautopilot = null, int? hightemperaturesuperconductors = null, int? generaloverhaulbattleship = null, int? artificialswarmintelligence = null, int? generaloverhaulbattlecruiser = null, int? generaloverhaulbomber = null, int? generaloverhauldestroyer = null, int? experimentalweaponstechnology = null, int? mechangeneralenhancement = null, int? heatrecovery = null, int? sulphideprocess = null, int? psionicnetwork = null, int? telekinetictractorbeam = null, int? enhancedsensortechnology = null, int? neuromodalcompressor = null, int? neurointerface = null, int? interplanetaryanalysisnetwork = null, int? overclockingheavyfighter = null, int? telekineticdrive = null, int? sixthsense = null, int? psychoharmoniser = null, int? efficientswarmintelligence = null, int? overclockinglargecargo = null, int? gravitationsensors = null, int? overclockingbattleship = null, int? psionicshieldmatrix = null, int? kaeleshdiscovererenhancement = null) {
			IntergalacticEnvoys = intergalacticenvoys ?? 0;
			HighPerformanceExtractors = highperformanceextractors ?? 0;
			FusionDrives = fusiondrives ?? 0;
			StealthFieldGenerator = stealthfieldgenerator ?? 0;
			OrbitalDen = orbitalden ?? 0;
			ResearchAI = researchai ?? 0;
			HighPerformanceTerraformer = highperformanceterraformer ?? 0;
			EnhancedProductionTechnologies = enhancedproductiontechnologies ?? 0;
			LightFighterMkII = lightfightermkii ?? 0;
			CruiserMkII = cruisermkii ?? 0;
			ImprovedLabTechnology = improvedlabtechnology ?? 0;
			PlasmaTerraformer = plasmaterraformer ?? 0;
			LowTemperatureDrives = lowtemperaturedrives ?? 0;
			BomberMkII = bombermkii ?? 0;
			DestroyerMkII = destroyermkii ?? 0;
			BattlecruiserMkII = battlecruisermkii ?? 0;
			RobotAssistants = robotassistants ?? 0;
			Supercomputer = supercomputer ?? 0;
			VolcanicBatteries = volcanicbatteries ?? 0;
			AcousticScanning = acousticscanning ?? 0;
			HighEnergyPumpSystems = highenergypumpsystems ?? 0;
			CargoHoldExpansionCivilianShips = cargoholdexpansioncivilianships ?? 0;
			MagmaPoweredProduction = magmapoweredproduction ?? 0;
			GeothermalPowerPlants = geothermalpowerplants ?? 0;
			DepthSounding = depthsounding ?? 0;
			IonCrystalEnhancementHeavyFighter = ioncrystalenhancementheavyfighter ?? 0;
			ImprovedStellarator = improvedstellarator ?? 0;
			HardenedDiamondDrillHeads = hardeneddiamonddrillheads ?? 0;
			SeismicMiningTechnology = seismicminingtechnology ?? 0;
			MagmaPoweredPumpSystems = magmapoweredpumpsystems ?? 0;
			IonCrystalModules = ioncrystalmodules ?? 0;
			OptimisedSiloConstructionMethod = optimisedsiloconstructionmethod ?? 0;
			DiamondEnergyTransmitter = diamondenergytransmitter ?? 0;
			ObsidianShieldReinforcement = obsidianshieldreinforcement ?? 0;
			RuneShields = runeshields ?? 0;
			RocktalCollectorEnhancement = rocktalcollectorenhancement ?? 0;
			CatalyserTechnology = catalysertechnology ?? 0;
			PlasmaDrive = plasmadrive ?? 0;
			EfficiencyModule = efficiencymodule ?? 0;
			DepotAI = depotai ?? 0;
			GeneralOverhaulLightFighter = generaloverhaullightfighter ?? 0;
			AutomatedTransportLines = automatedtransportlines ?? 0;
			ImprovedDroneAI = improveddroneai ?? 0;
			ExperimentalRecyclingTechnology = experimentalrecyclingtechnology ?? 0;
			GeneralOverhaulCruiser = generaloverhaulcruiser ?? 0;
			SlingshotAutopilot = slingshotautopilot ?? 0;
			HighTemperatureSuperconductors = hightemperaturesuperconductors ?? 0;
			GeneralOverhaulBattleship = generaloverhaulbattleship ?? 0;
			ArtificialSwarmIntelligence = artificialswarmintelligence ?? 0;
			GeneralOverhaulBattlecruiser = generaloverhaulbattlecruiser ?? 0;
			GeneralOverhaulBomber = generaloverhaulbomber ?? 0;
			GeneralOverhaulDestroyer = generaloverhauldestroyer ?? 0;
			ExperimentalWeaponsTechnology = experimentalweaponstechnology ?? 0;
			MechanGeneralEnhancement = mechangeneralenhancement ?? 0;
			HeatRecovery = heatrecovery ?? 0;
			SulphideProcess = sulphideprocess ?? 0;
			PsionicNetwork = psionicnetwork ?? 0;
			TelekineticTractorBeam = telekinetictractorbeam ?? 0;
			EnhancedSensorTechnology = enhancedsensortechnology ?? 0;
			NeuromodalCompressor = neuromodalcompressor ?? 0;
			NeuroInterface = neurointerface ?? 0;
			InterplanetaryAnalysisNetwork = interplanetaryanalysisnetwork ?? 0;
			OverclockingHeavyFighter = overclockingheavyfighter ?? 0;
			TelekineticDrive = telekineticdrive ?? 0;
			SixthSense = sixthsense ?? 0;
			Psychoharmoniser = psychoharmoniser ?? 0;
			EfficientSwarmIntelligence = efficientswarmintelligence ?? 0;
			OverclockingLargeCargo = overclockinglargecargo ?? 0;
			GravitationSensors = gravitationsensors ?? 0;
			OverclockingBattleship = overclockingbattleship ?? 0;
			PsionicShieldMatrix = psionicshieldmatrix ?? 0;
			KaeleshDiscovererEnhancement = kaeleshdiscovererenhancement ?? 0;
		}

		public int GetLevel(LFTechno building) {
			int output = 0;
			foreach (PropertyInfo prop in GetType().GetProperties()) {
				if (prop.Name == building.ToString()) {
					object value = prop.GetValue(this, null);
					if (value is not null) {
						output = (int) prop.GetValue(this, null);
					}
				}
			}
			return output;
		}

		public LFTechs SetLevel(LFTechno buildable, int? level) {
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if (prop.Name == buildable.ToString()) {
					prop.SetValue(this, level);
				}
			}

			return this;
		}
	}

}
