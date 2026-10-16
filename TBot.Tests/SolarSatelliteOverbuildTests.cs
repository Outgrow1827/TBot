using Microsoft.Extensions.Logging;
using Serilog.Events;
using Tbot.Includes;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	// Real incident 2026-10-15: CalcNeededSolarSatellites computed a need of 2,303,257 satellites
	// from a single bad/extreme energy reading and had no sanity check before that number was
	// turned into a real construction order (the game itself only stopped it at its own 99999 cap,
	// which still wiped the account's resources). These tests pin the fix: the computed order must
	// never exceed the planet's existing SolarSatellite count plus a small safety margin.
	public class SolarSatelliteOverbuildTests {
		private class NoOpLogger : ILoggerService<CalculationService> {
			public void WriteLog(LogLevel level, LogSender source, string message) { }
			public void ConfigureLogging(string logPath) { }
			public void AddTelegramLogger(string botToken, string chatId) { }
			public void RemoveTelegramLogger() { }
			public bool IsTelegramLoggerEnabled() => false;
			public void SetTelegramLoggerLogLevel(LogEventLevel logLevel) { }
			public LogEventLevel GetTelegramLoggerLevel() => LogEventLevel.Information;
		}

		private static CalculationService NewService() {
			return new CalculationService(new NoOpLogger(), ogameService: null);
		}

		private static Planet NewPlanet(long existingSatellites, int minTemp = 20, int maxTemp = 40) {
			return new Planet {
				Coordinate = new Coordinate(1, 1, 1),
				Ships = new Ships { SolarSatellite = existingSatellites },
				Resources = new Resources(0, 0, 0, 0),
				Temperature = new Temperature { Min = minTemp, Max = maxTemp },
			};
		}

		[Fact]
		public void RealIncidentMagnitudeDeficitGetsCappedInsteadOfOrderingMillions() {
			var service = NewService();
			var planet = NewPlanet(existingSatellites: 0);

			// GetSolarSatelliteOutput at avg temp 30 is (30+160)/6 = ~32/satellite. A deficit large
			// enough to need ~2.3M satellites is in the tens of millions - exactly the real incident.
			long implausibleDeficit = 70_000_000;

			int needed = service.CalcNeededSolarSatellites(planet, implausibleDeficit);

			Assert.True(needed <= 50, $"expected the order to be capped near the safety margin, got {needed}");
		}

		[Fact]
		public void CapScalesWithExistingSatelliteCount() {
			var service = NewService();
			var planet = NewPlanet(existingSatellites: 1000);

			int needed = service.CalcNeededSolarSatellites(planet, requiredEnergy: 70_000_000);

			Assert.Equal(1050, needed); // 1000 existing + 50 margin
		}

		[Fact]
		public void PlausibleSmallDeficitIsNotCapped() {
			var service = NewService();
			var planet = NewPlanet(existingSatellites: 0);

			// avg temp 30 -> ~32 energy/satellite; a 320 deficit needs ~10 satellites, well under cap.
			int needed = service.CalcNeededSolarSatellites(planet, requiredEnergy: 320);

			Assert.True(needed > 0 && needed <= 50, $"expected a small plausible count, got {needed}");
		}

		[Fact]
		public void ZeroOutputFromExtremeTemperatureDoesNotProduceGarbageCount() {
			var service = NewService();
			// Min=Max=-160 => Average=-160 => GetSolarSatelliteOutput's (-160+160)/6 = 0.
			var planet = NewPlanet(existingSatellites: 5, minTemp: -160, maxTemp: -160);

			int needed = service.CalcNeededSolarSatellites(planet, requiredEnergy: 1000);

			Assert.True(needed >= 0 && needed <= 55, $"expected a safe capped value, got {needed}");
		}

		[Fact]
		public void NoDeficitAtAllNeedsZeroSatellites() {
			var service = NewService();
			var planet = NewPlanet(existingSatellites: 0);
			planet.Resources.Energy = 500; // surplus

			int needed = service.CalcNeededSolarSatellites(planet, requiredEnergy: 0);

			Assert.Equal(0, needed);
		}
	}
}
