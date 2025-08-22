using System;
using System.Collections.Generic;
using System.Linq;
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

		public LFBuildings(int lifeformType = 0, int residentialsector = 0, int biospherefarm = 0, int researchcentre = 0, int academyofsciences = 0, int neurocalibrationcentre = 0, int highenergysmelting = 0, int foodsilo = 0, int fusionpoweredproduction = 0, int skyscraper = 0, int biotechlab = 0, int metropolis = 0, int planetaryshield = 0, int meditationenclave = 0, int crystalfarm = 0, int runetechnologium = 0, int runeforge = 0, int oriktorium = 0, int magmaforge = 0, int disruptionchamber = 0, int megalith = 0, int crystalrefinery = 0, int deuteriumsynthesiser = 0, int mineralresearchcentre = 0, int advancedrecyclingplant = 0, int assemblyline = 0, int fusioncellfactory = 0, int roboticsresearchcentre = 0, int updatenetwork = 0, int quantumcomputercentre = 0, int automatisedassemblycentre = 0, int highperformancetransformer = 0, int microchipassemblyline = 0, int productionassemblyhall = 0, int highperformancesynthesiser = 0, int chipmassproduction = 0, int nanorepairbots = 0, int sanctuary = 0, int antimattercondenser = 0, int vortexchamber = 0, int hallsofrealisation = 0, int forumoftranscendence = 0, int antimatterconvector = 0, int cloninglaboratory = 0, int chrysalisaccelerator = 0, int biomodifier = 0, int psionicmodulator = 0, int shipmanufacturinghall = 0, int suprarefractor = 0) {
			LifeformType = lifeformType;
			ResidentialSector = residentialsector;
			BiosphereFarm = biospherefarm;
			ResearchCentre = researchcentre;
			AcademyOfSciences = academyofsciences;
			NeuroCalibrationCentre = neurocalibrationcentre;
			HighEnergySmelting = highenergysmelting;
			FoodSilo = foodsilo;
			FusionPoweredProduction = fusionpoweredproduction;
			Skyscraper = skyscraper;
			BiotechLab = biotechlab;
			Metropolis = metropolis;
			PlanetaryShield = planetaryshield;
			MeditationEnclave = meditationenclave;
			CrystalFarm = crystalfarm;
			RuneTechnologium = runetechnologium;
			RuneForge = runeforge;
			Oriktorium = oriktorium;
			MagmaForge = magmaforge;
			DisruptionChamber = disruptionchamber;
			Megalith = megalith;
			CrystalRefinery = crystalrefinery;
			DeuteriumSynthesiser = deuteriumsynthesiser;
			MineralResearchCentre = mineralresearchcentre;
			AdvancedRecyclingPlant = advancedrecyclingplant;
			AssemblyLine = assemblyline;
			FusionCellFactory = fusioncellfactory;
			RoboticsResearchCentre = roboticsresearchcentre;
			UpdateNetwork = updatenetwork;
			QuantumComputerCentre = quantumcomputercentre;
			AutomatisedAssemblyCentre = automatisedassemblycentre;
			HighPerformanceTransformer = highperformancetransformer;
			MicrochipAssemblyLine = microchipassemblyline;
			ProductionAssemblyHall = productionassemblyhall;
			HighPerformanceSynthesiser = highperformancesynthesiser;
			ChipMassProduction = chipmassproduction;
			NanoRepairBots = nanorepairbots;
			Sanctuary = sanctuary;
			AntimatterCondenser = antimattercondenser;
			VortexChamber = vortexchamber;
			HallsOfRealisation = hallsofrealisation;
			ForumOfTranscendence = forumoftranscendence;
			AntimatterConvector = antimatterconvector;
			CloningLaboratory = cloninglaboratory;
			ChrysalisAccelerator = chrysalisaccelerator;
			BioModifier = biomodifier;
			PsionicModulator = psionicmodulator;
			ShipManufacturingHall = shipmanufacturinghall;
			SupraRefractor = suprarefractor;
		}

		public int GetLevel(LFBuildables building) {
			int output = 0;
			foreach (PropertyInfo prop in GetType().GetProperties()) {
				if (prop.Name == building.ToString()) {
					output = (int) prop.GetValue(this);
				}
			}
			return output;
		}

		public LFBuildings SetLevel(LFBuildables buildable, int level) {
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if (prop.Name == buildable.ToString()) {
					prop.SetValue(this, level);
				}
			}

			return this;
		}
	}

}
