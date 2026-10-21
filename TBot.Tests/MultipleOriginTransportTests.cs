using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	// CalcMaxTransportableResources fills the hold deuterium-first, so a deut-rich origin hid its metal/crystal.
	public class MultipleOriginTransportTests {
		private class NoOpLogger : ILoggerService<CalculationService> {
			public void WriteLog(LogLevel level, LogSender source, string message) { }
			public void ConfigureLogging(string logPath) { }
			public void AddTelegramLogger(string botToken, string chatId) { }
			public void RemoveTelegramLogger() { }
			public bool IsTelegramLoggerEnabled() => false;
			public void SetTelegramLoggerLogLevel(LogEventLevel logLevel) { }
			public LogEventLevel GetTelegramLoggerLevel() => LogEventLevel.Information;
		}

		[Fact]
		public void OriginWithMoreDeuteriumThanCargoCapacityStillShipsMetalAndCrystal() {
			var service = new CalculationService(new NoOpLogger(), ogameService: null);
			var homeworld = new Planet {
				ID = 1,
				Coordinate = new Coordinate(5, 380, 4),
				Resources = new Resources(4_962_491_465, 4_667_898_349, 1_237_141_323),
				Ships = new Ships { LargeCargo = 616 },
				LFBonuses = new LFBonuses(),
			};
			var colony = new Planet {
				ID = 2,
				Coordinate = new Coordinate(8, 201, 6),
				Resources = new Resources(1_380_000, 380_123, 141_915),
				Ships = new Ships(),
				LFBonuses = new LFBonuses(),
			};
			var userData = new UserData {
				serverData = new ServerData { Galaxies = 9, Systems = 499, SpeedFleetPeaceful = 1, SpeedFleetWar = 1, SpeedFleetHolding = 1, GlobalDeuteriumSaveFactor = 1 },
				researches = new Researches(),
				userInfo = new UserInfo(),
			};
			var settings = new TransportSettings(
				active: true, cargoType: Buildables.LargeCargo, roundResources: false, sendToTheMoonIfPossible: false,
				origin: homeworld, maxSlots: 2,
				multipleOrigins: new MultipleOrigins(active: true, onlyFromMoons: false, minimumResourcesToSend: 0, priorityToP: false, exclude: new List<Celestial>()));
			var missing = new Resources(620_000, 619_877, 58_085);

			var result = service.CalcMultipleOrigin(colony, new List<Celestial> { homeworld, colony }, missing, settings, new List<Fleet>(), userData);

			Assert.Single(result);
			var shipment = result[0].Single();
			Assert.Equal(1, shipment.Key.ID);
			Assert.Equal(620_000, shipment.Value.Metal);
			Assert.Equal(619_877, shipment.Value.Crystal);
		}
	}
}
