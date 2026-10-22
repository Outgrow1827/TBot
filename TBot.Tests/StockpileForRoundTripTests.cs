using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	// Real case 2026-10-21: MetalMine 24 on 5:201:10 lacked ~89k crystal, but the stockpile shipped
	// MetalMine 24-31 (19.0M metal + 8.2M crystal) because it projected one building only, uncapped.
	public class StockpileForRoundTripTests {
		private class NoOpLogger : ILoggerService<CalculationService> {
			public void WriteLog(LogLevel level, LogSender source, string message) { }
			public void ConfigureLogging(string logPath) { }
			public void AddTelegramLogger(string botToken, string chatId) { }
			public void RemoveTelegramLogger() { }
			public bool IsTelegramLoggerEnabled() => false;
			public void SetTelegramLoggerLogLevel(LogEventLevel logLevel) { }
			public LogEventLevel GetTelegramLoggerLevel() => LogEventLevel.Information;
		}

		private static readonly ServerData Server = new() { Galaxies = 9, Systems = 499, SpeedFleetPeaceful = 1, SpeedFleetWar = 1, SpeedFleetHolding = 1, GlobalDeuteriumSaveFactor = 1 };
		private static readonly Planet Colony = new() { Coordinate = new Coordinate(5, 201, 10), LFBonuses = new LFBonuses() };

		private static Resources Stockpile(IEnumerable<(Resources, long)> plan) =>
			BrainTransportCoordinator.CalcResourcesForRoundTrip(new CalculationService(new NoOpLogger(), ogameService: null), Colony,
				new Coordinate(5, 380, 4), Buildables.LargeCargo, new Researches(), Server, CharacterClass.NoClass, AllianceClass.NoClass, plan);

		[Fact]
		public void WindowStopsAtTheNextSleep() {
			// 100 s per construction, sleep in 250 s: steps start at 0, 100, 200 -> 3 covered
			var plan = Enumerable.Range(0, 10).Select(_ => (new Resources(1_000, 0, 0), 100L));

			var total = BrainTransportCoordinator.CalcResourcesForRoundTrip(new CalculationService(new NoOpLogger(), ogameService: null), Colony,
				new Coordinate(5, 380, 4), Buildables.LargeCargo, new Researches(), Server, CharacterClass.NoClass, AllianceClass.NoClass, plan,
				secondsUntilSleep: 250);

			Assert.Equal(3_000, total.Metal);
		}

		[Fact]
		public void SleepTimeIsTheNextGoToSleep() {
			dynamic settings = Newtonsoft.Json.JsonConvert.DeserializeObject("{\"SleepMode\":{\"Active\":true,\"GoToSleep\":\"23:45\"}}");

			Assert.Equal(2 * 3600 + 45 * 60, BrainTransportCoordinator.SecondsUntilSleep(settings, new System.DateTime(2026, 10, 21, 21, 0, 0)));
			Assert.Equal(23 * 3600 + 45 * 60, BrainTransportCoordinator.SecondsUntilSleep(settings, new System.DateTime(2026, 10, 21, 0, 0, 0)));
		}

		[Fact]
		public void MixedSequenceKeepsEachBuildingsOwnResourceMix() {
			var plan = new[] {
				(new Resources(600_000, 150_000, 0), 0L),
				(new Resources(400_000, 200_000, 0), 0L),
				(new Resources(300_000, 100_000, 0), 0L),
			};

			var total = Stockpile(plan);

			Assert.Equal(1_300_000, total.Metal);
			Assert.Equal(450_000, total.Crystal);
		}

		[Fact]
		public void FlightFuelIsNotShippedToTheColony() {
			// 2:201:9 kept 2.4M idle deut while every transport delivered the round-trip fuel again
			var total = Stockpile(new[] { (new Resources(63_000_000, 22_000_000, 1_638_400), 0L) });

			Assert.Equal(1_638_400, total.Deuterium);
		}

		[Fact]
		public void FirstStepIsAlwaysCovered() {
			var total = Stockpile(new[] { (new Resources(673_365, 168_342, 0), 999_999L) });

			Assert.Equal(673_365, total.Metal);
			Assert.Equal(168_342, total.Crystal);
		}
	}
}
