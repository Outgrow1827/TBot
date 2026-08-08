using System;
using System.Collections.Generic;
using System.IO;
using TBot.Model;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
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
		public void DiscoveryPlannerOnlyReturnsPositionsReportedByTheSystemView() {
			var positions = DiscoverySystemPlanner.SelectAvailablePositions(
				new[] { 1, 3, 7, 16 },
				1,
				new HashSet<int>(),
				3);

			Assert.Equal(new[] { 1, 3, 7 }, positions);
		}

		[Fact]
		public void DiscoveryPlannerSkipsBlacklistedPositionsAndResumesFromCursor() {
			var positions = DiscoverySystemPlanner.SelectAvailablePositions(
				new[] { 1, 3, 5, 9 },
				3,
				new HashSet<int> { 5 },
				3);

			Assert.Equal(new[] { 3, 9 }, positions);
		}

		[Fact]
		public void AutoFarmTreatsAPlanetsOnlySystemAsEmpty() {
			var planets = new[] {
				new Planet {
					Inactive = true,
					Vacation = true,
					Coordinate = new Coordinate(1, 1, 1, Celestials.Planet)
				}
			};

			Assert.True(AutoFarmSystemPolicy.IsEmptySystem(planets));
			Assert.Empty(AutoFarmSystemPolicy.GetEligibleTargets(planets));
		}

		[Fact]
		public void AutoFarmDoesNotUseTheLongCooldownForARegularInactivePlanet() {
			var planets = new[] {
				new Planet {
					Inactive = true,
					Vacation = false,
					Coordinate = new Coordinate(1, 1, 1, Celestials.Planet)
				}
			};

			Assert.False(AutoFarmSystemPolicy.IsEmptySystem(planets));
			Assert.Single(AutoFarmSystemPolicy.GetEligibleTargets(planets));
		}

		[Fact]
		public void AutoFarmDistinguishesARealEmptySystemFromAVisitedSystemWithoutTargets() {
			var activePlanet = new Planet {
				Inactive = false,
				Coordinate = new Coordinate(1, 1, 1, Celestials.Planet)
			};

			Assert.False(AutoFarmSystemPolicy.IsEmptySystem(new[] { activePlanet }));
		Assert.Empty(AutoFarmSystemPolicy.GetEligibleTargets(new[] { activePlanet }));
		Assert.True(AutoFarmSystemPolicy.IsEmptySystem(Array.Empty<Planet>()));
		}

		[Fact]
		public void AutoFarmKeepsEmptySystemsSkippedForThirtyDaysButUsesNormalDataForSevenDays() {
			var observedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
			var emptySnapshot = new AutoFarmSystemSnapshot {
				ObservedAtUtc = observedAt,
				IsEmpty = true
			};
			var populatedSnapshot = new AutoFarmSystemSnapshot {
				ObservedAtUtc = observedAt,
				IsEmpty = false
			};

			Assert.Equal(
				AutoFarmSystemCacheDecision.Skip,
				AutoFarmSystemCachePolicy.GetDecision(emptySnapshot, observedAt.AddDays(14), TimeSpan.FromDays(7), TimeSpan.FromDays(30)));
			Assert.Equal(
				AutoFarmSystemCacheDecision.UseSnapshot,
				AutoFarmSystemCachePolicy.GetDecision(populatedSnapshot, observedAt.AddDays(6), TimeSpan.FromDays(7), TimeSpan.FromDays(30)));
			Assert.Equal(
				AutoFarmSystemCacheDecision.Refresh,
				AutoFarmSystemCachePolicy.GetDecision(populatedSnapshot, observedAt.AddDays(7), TimeSpan.FromDays(7), TimeSpan.FromDays(30)));
			Assert.Equal(
				AutoFarmSystemCacheDecision.Refresh,
				AutoFarmSystemCachePolicy.GetDecision(emptySnapshot, observedAt.AddDays(30), TimeSpan.FromDays(7), TimeSpan.FromDays(30)));
		}

		[Fact]
		public void AutoFarmResumesACompletedRangeWithoutSkippingSystemsBeforeTheCursor() {
			var systems = AutoFarmScanPlanner.EnumerateSystems(1, 5, 3);

			Assert.Equal(new[] { 3, 4, 5, 1, 2 }, systems);
		}

		[Fact]
		public void AutoFarmSlotBudgetCountsItsEspionageAndAttackFleets() {
			var fleets = new[] {
				new Fleet { Mission = Missions.Spy, Ships = new Ships(espionageProbe: 52) },
				new Fleet { Mission = Missions.Attack, Ships = new Ships(largeCargo: 25) },
				new Fleet { Mission = Missions.Attack, Ships = new Ships(largeCargo: 20) },
			};

			var budget = AutoFarmSlotPlanner.Calculate(
				maxSlots: 5,
				totalFreeSlots: 20,
				slotsToLeaveFree: 1,
				fleets,
				Buildables.LargeCargo);

			Assert.Equal(3, budget.OwnedSlots);
			Assert.Equal(2, budget.AvailableSlots);
		}

		[Fact]
		public void AutoFarmSlotBudgetNeverUsesASlotReservedForOtherWorkers() {
			var budget = AutoFarmSlotPlanner.Calculate(
				maxSlots: 5,
				totalFreeSlots: 1,
				slotsToLeaveFree: 1,
				Array.Empty<Fleet>(),
				Buildables.LargeCargo);

			Assert.Equal(0, budget.AvailableSlots);
		}

		[Fact]
		public void AutoFarmSlotBudgetDoesNotClaimMixedManualAttackFleets() {
			var fleets = new[] {
				new Fleet {
					Mission = Missions.Attack,
					Ships = new Ships(largeCargo: 25, battleship: 1)
				}
			};

			var budget = AutoFarmSlotPlanner.Calculate(
				maxSlots: 1,
				totalFreeSlots: 10,
				slotsToLeaveFree: 0,
				fleets,
				Buildables.LargeCargo);

			Assert.Equal(0, budget.OwnedSlots);
			Assert.Equal(1, budget.AvailableSlots);
		}

		[Fact]
		public void AutoFarmStateStorePersistsSystemSnapshotsAndPendingTargets() {
			var dataFolder = Path.Combine(Path.GetTempPath(), "tbot-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dataFolder);

			try {
				var observedAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
				using (var store = new AutoFarmStateStore("autofarm.db", dataFolder)) {
					store.SaveSystemSnapshot(5, 123, observedAt, false, new List<Planet> {
						new Planet {
							Name = "Inactive Planet",
							Inactive = true,
							Coordinate = new Coordinate(5, 123, 7, Celestials.Planet)
						}
					});

					store.UpsertTarget(new FarmTarget(
						new Planet { Name = "Inactive Planet", Coordinate = new Coordinate(5, 123, 7, Celestials.Planet) },
						FarmState.AttackPending,
						new EspionageReport {
							Coordinate = new Coordinate(5, 123, 7, Celestials.Planet),
							Metal = 1000000
						}),
						observedAt);
				}

				using (var store = new AutoFarmStateStore("autofarm.db", dataFolder)) {
					var snapshot = store.GetFreshSystemSnapshot(5, 123, observedAt.AddDays(6), TimeSpan.FromDays(7));
					var targets = store.LoadTargets();

					Assert.NotNull(snapshot);
					Assert.Single(snapshot.Planets);
					Assert.Single(targets);
					Assert.Equal(FarmState.AttackPending, targets[0].State);
					Assert.Equal(1000000, targets[0].Report.Metal);
				}
			} finally {
				Directory.Delete(dataFolder, true);
			}
		}

		[Fact]
		public void AutoFarmStateStoreTreatsOldSystemSnapshotsAsStale() {
			var dataFolder = Path.Combine(Path.GetTempPath(), "tbot-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dataFolder);

			try {
				var observedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
				using var store = new AutoFarmStateStore("autofarm.db", dataFolder);
				store.SaveSystemSnapshot(5, 123, observedAt, true, new List<Planet>());

				Assert.Null(store.GetFreshSystemSnapshot(5, 123, observedAt.AddDays(7), TimeSpan.FromDays(7)));
			} finally {
				Directory.Delete(dataFolder, true);
			}
		}
	}
}
