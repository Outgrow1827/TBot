using System;
using TBot.Model;
using Tbot.Workers;
using Xunit;

namespace TBot.Tests {
	public class MissionPlanningTests {
		[Fact]
		public void DiscoveryCursorMovesToNextSystemAfterAllFifteenPositionsAreExamined() {
			var cursor = new DiscoveryCursorState(42);

			for (var position = 1; position <= 15; position++)
				cursor.CommitPosition(42, position, 100);

			Assert.Equal(43, cursor.System);
			Assert.Equal(1, cursor.NextPosition);
		}

		[Fact]
		public void DiscoveryCursorAdvancesWhenAStartingPositionIsSkipped() {
			var cursor = new DiscoveryCursorState(42, 1);

			cursor.CommitPosition(42, 1, 100);

			Assert.Equal(42, cursor.System);
			Assert.Equal(2, cursor.NextPosition);
		}

		[Fact]
		public void DiscoveryCursorWrapsAfterTheLastSystem() {
			var cursor = new DiscoveryCursorState(100, 15);

			cursor.CommitPosition(100, 15, 100);

			Assert.Equal(1, cursor.System);
			Assert.Equal(1, cursor.NextPosition);
		}

		[Fact]
		public void ExpeditionPlanIsFairAndRespectsEveryOriginCapacity() {
			var plan = ExpeditionOriginPlanner.BuildRoundRobinPlan(new[] { 1, 1, 1 }, 3);

			Assert.Equal(new[] { 1, 1, 1 }, plan);
		}

		[Fact]
		public void ExpeditionPlanDoesNotAssignToFullOrigins() {
			var plan = ExpeditionOriginPlanner.BuildRoundRobinPlan(new[] { 0, 1, 1 }, 3);

			Assert.Equal(new[] { 0, 1, 1 }, plan);
		}

		[Fact]
		public void ExpeditionPlanUsesSpareCapacityBeforeGivingUpRequestedFleets() {
			var plan = ExpeditionOriginPlanner.BuildRoundRobinPlan(new[] { 2, 2 }, 3);

			Assert.Equal(new[] { 1, 2 }, plan);
		}

		[Fact]
		public void EmptySystemCacheSuppressesARecentlyScannedEmptySystem() {
			var cache = new AutoFarmEmptySystemCache();
			var scannedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

			cache.MarkEmpty(5, 123, TimeSpan.FromHours(6), scannedAt);

			Assert.True(cache.IsCoolingDown(5, 123, scannedAt.AddHours(5)));
			Assert.False(cache.IsCoolingDown(5, 124, scannedAt.AddHours(5)));
		}

		[Fact]
		public void EmptySystemCacheExpiresAfterTheConfiguredCooldown() {
			var cache = new AutoFarmEmptySystemCache();
			var scannedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

			cache.MarkEmpty(5, 123, TimeSpan.FromHours(6), scannedAt);

			Assert.False(cache.IsCoolingDown(5, 123, scannedAt.AddHours(6)));
		}

		[Fact]
		public void NonEmptySystemClearsItsCooldown() {
			var cache = new AutoFarmEmptySystemCache();
			var scannedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

			cache.MarkEmpty(5, 123, TimeSpan.FromHours(6), scannedAt);
			cache.MarkNonEmpty(5, 123);

			Assert.False(cache.IsCoolingDown(5, 123, scannedAt.AddHours(1)));
		}
	}
}
