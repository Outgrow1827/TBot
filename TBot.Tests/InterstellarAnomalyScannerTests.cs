using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Tbot.Includes;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	public class InterstellarAnomalyScannerTests {
		private class NoOpLogger : ILoggerService<CalculationService> {
			public void WriteLog(LogLevel level, LogSender source, string message) { }
			public void ConfigureLogging(string logPath) { }
			public void AddTelegramLogger(string botToken, string chatId) { }
			public void RemoveTelegramLogger() { }
			public bool IsTelegramLoggerEnabled() => false;
			public void SetTelegramLoggerLogLevel(LogEventLevel logLevel) { }
			public LogEventLevel GetTelegramLoggerLevel() => LogEventLevel.Information;
		}

		private static readonly CalculationService Calc = new(new NoOpLogger(), ogameService: null);

		// costs read from the game's technologydetails page (2026-10-17 and 2026-10-21 captures)
		[Theory]
		[InlineData(1, 90, 45, 15)]
		[InlineData(4, 303, 151, 50)]
		[InlineData(6, 683, 341, 113)]
		[InlineData(11, 5189, 2594, 864)]
		public void CostMatchesTheGame(int level, long metal, long crystal, long deuterium) {
			var cost = Calc.CalcPrice(Buildables.InterstellarAnomalyScanner, level);

			Assert.Equal(metal, cost.Metal);
			Assert.Equal(crystal, cost.Crystal);
			Assert.Equal(deuterium, cost.Deuterium);
		}

		private static Planet NewPlanet(int robotics) => new() {
			Coordinate = new Coordinate(5, 201, 10),
			Buildings = new Buildings(),
			Facilities = new Facilities { RoboticsFactory = robotics, Shipyard = 12, ResearchLab = 14, NaniteFactory = 6, SpaceDock = 6, Terraformer = 0, InterstellarAnomalyScanner = 3 },
			Productions = new List<Production>(),
			Constructions = new Constructions(),
			Fields = new Fields { Built = 10, Total = 300 },
			Resources = new Resources(),
			ResourcesProduction = new ResourcesProduction(),
			LFBonuses = new LFBonuses(),
			Temperature = new Temperature { Min = 20, Max = 40 },
		};

		private static Buildables Next(Planet planet) => Calc.GetNextFacilityToBuild(planet,
			new Researches { Astrophysics = 6, CombustionDrive = 6, ComputerTechnology = 10 },
			new Buildings(),
			new Facilities { RoboticsFactory = 14, Shipyard = 12, ResearchLab = 14, NaniteFactory = 6, SpaceDock = 6, MissileSilo = 0, Terraformer = 99, InterstellarAnomalyScanner = 50 },
			CharacterClass.NoClass, new Staff(), new ServerData { Speed = 1 }, new AutoMinerSettings());

		[Fact]
		public void BuiltOnlyAfterEveryOtherFacilityIsMaxedTerraformerIgnored() {
			Assert.Equal(Buildables.InterstellarAnomalyScanner, Next(NewPlanet(robotics: 14)));
		}

		[Fact]
		public void NotBuiltWhileAFacilityIsBelowItsMax() {
			Assert.NotEqual(Buildables.InterstellarAnomalyScanner, Next(NewPlanet(robotics: 13)));
		}
	}
}
