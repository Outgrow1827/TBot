using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Tbot.Common.Settings;
using TBot.Model;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using TBot.WebUI.Services;
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
		public void OrderedWorkerScheduleNeverReversesThePlannedOrder() {
			var first = OrderedWorkerSchedule.NextDueTime(TimeSpan.Zero, TimeSpan.FromSeconds(2));
			var second = OrderedWorkerSchedule.NextDueTime(first, TimeSpan.FromSeconds(5));
			var third = OrderedWorkerSchedule.NextDueTime(second, TimeSpan.FromSeconds(1));

			Assert.True(first < second);
			Assert.True(second < third);
			Assert.Equal(TimeSpan.FromSeconds(8), third);
		}

		[Fact]
		public void OrderedWorkerScheduleIgnoresAnInvalidNegativeDelay() {
			Assert.Equal(
				TimeSpan.FromSeconds(3),
				OrderedWorkerSchedule.NextDueTime(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(-1)));
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
		public void AutoFarmTreatsAnAttackFromAnyOriginAsActive() {
			var target = new FarmTarget(
				new Planet { Coordinate = new Coordinate(2, 200, 8, Celestials.Planet) },
				FarmState.AttackSent);
			var fleets = new[] {
				new Fleet {
					Mission = Missions.Attack,
					Origin = new Coordinate(2, 10, 3, Celestials.Planet),
					Destination = new Coordinate(2, 200, 8, Celestials.Planet),
					ReturnFlight = true
				}
			};

			Assert.True(AutoFarmAttackPolicy.IsAttackInProgress(target, fleets));
		}

		[Fact]
		public void AutoFarmNeverReusesAConsumedEspionageReport() {
			var target = new FarmTarget(
				new Planet { Coordinate = new Coordinate(2, 200, 8, Celestials.Planet) },
				FarmState.ProbesPending) {
				ConsumedReportId = 42
			};

			Assert.False(AutoFarmAttackPolicy.CanProcessReport(target, new EspionageReport { ID = 42 }));
			Assert.True(AutoFarmAttackPolicy.CanProcessReport(target, new EspionageReport { ID = 43 }));
		}

		[Fact]
		public void AutoFarmDoesNotProcessReportsWhileThePreviousAttackIsStillSent() {
			var target = new FarmTarget(
				new Planet { Coordinate = new Coordinate(2, 200, 8, Celestials.Planet) },
				FarmState.AttackSent) {
				ConsumedReportId = 42
			};

			Assert.False(AutoFarmAttackPolicy.CanProcessReport(target, new EspionageReport { ID = 42 }));
			Assert.False(AutoFarmAttackPolicy.CanProcessReport(target, new EspionageReport { ID = 43 }));
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

					var persistedTarget = new FarmTarget(
						new Planet { Name = "Inactive Planet", Coordinate = new Coordinate(5, 123, 7, Celestials.Planet) },
						FarmState.AttackPending,
						new EspionageReport {
							Coordinate = new Coordinate(5, 123, 7, Celestials.Planet),
							Metal = 1000000,
							ID = 42
						}) {
						ConsumedReportId = 42
					};
					store.UpsertTarget(persistedTarget,
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
					Assert.Equal(42, targets[0].ConsumedReportId);
				}
			} finally {
				Directory.Delete(dataFolder, true);
			}
		}

		[Fact]
		public void AutoFarmStateStorePersistsConsumedReportsAndDeduplicatesAttackHistory() {
			var dataFolder = Path.Combine(Path.GetTempPath(), "tbot-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dataFolder);

			try {
				var dispatchedAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
				var target = new FarmTarget(
					new Planet { Name = "Inactive Planet", Coordinate = new Coordinate(5, 123, 7, Celestials.Planet) },
					FarmState.AttackSent,
					new EspionageReport {
						Coordinate = new Coordinate(5, 123, 7, Celestials.Planet),
						ID = 99
					});

				using (var store = new AutoFarmStateStore("autofarm.db", dataFolder)) {
					store.RecordAttack(target, new Resources(metal: 100, crystal: 50, deuterium: 25), dispatchedAt);
					store.RecordAttack(target, new Resources(metal: 100, crystal: 50, deuterium: 25), dispatchedAt);
				}

				using (var store = new AutoFarmStateStore("autofarm.db", dataFolder)) {
					Assert.True(store.HasConsumedReport(99));
					var history = store.LoadAttackHistory();
					Assert.Single(history);
					Assert.Equal(99, history[0].ReportId);
					Assert.Equal(175, history[0].Loot.TotalResources);
				}
			} finally {
				Directory.Delete(dataFolder, true);
			}
		}

		[Fact]
		public void AutoFarmStateStorePersistsScanCursorAcrossRestart() {
			var dataFolder = Path.Combine(Path.GetTempPath(), "tbot-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dataFolder);

			try {
				var updatedAt = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
				using (var store = new AutoFarmStateStore("autofarm.db", dataFolder))
					store.SaveScanCursor(0, 1, 139, updatedAt);

				using (var store = new AutoFarmStateStore("autofarm.db", dataFolder)) {
					var cursor = store.LoadScanCursor();
					Assert.NotNull(cursor);
					Assert.Equal(new AutoFarmScanCursor(0, 1, 139), cursor);
				}
			} finally {
				Directory.Delete(dataFolder, true);
			}
		}

		[Fact]
		public void AutoFarmDoesNotReprocessTheSameNonActionableReportWhenDeletionFails() {
			var coordinate = new Coordinate(1, 139, 7, Celestials.Planet);
			var target = new FarmTarget(
				new Planet { Name = "Inactive Planet", Coordinate = coordinate },
				FarmState.NotSuitable,
				new EspionageReport { ID = 42, Coordinate = coordinate });

			Assert.True(AutoFarmAttackPolicy.IsRepeatedNonActionableReport(target,
				new EspionageReport { ID = 42, Coordinate = coordinate }));
			Assert.False(AutoFarmAttackPolicy.IsRepeatedNonActionableReport(target,
				new EspionageReport { ID = 43, Coordinate = coordinate }));
		}

		[Fact]
		public void AutoFarmDoesNotCrashWhenAFoundTargetHasNoPreviousReport() {
			var coordinate = new Coordinate(1, 139, 7, Celestials.Planet);
			var target = new FarmTarget(
				new Planet { Name = "Inactive Planet", Coordinate = coordinate },
				FarmState.ProbesPending);

			Assert.False(AutoFarmAttackPolicy.IsRepeatedNonActionableReport(target,
				new EspionageReport { ID = 42, Coordinate = coordinate }));
		}

		[Fact]
		public void AutoFarmDashboardReaderShowsCurrentReportsAndAttackHistory() {
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			Directory.CreateDirectory(dataFolder);
			var databasePath = Path.Combine(dataFolder, "autofarm_dashboard-test.db");

			try {
				var observedAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
				var target = new FarmTarget(
					new Planet { Name = "Dashboard Target", Coordinate = new Coordinate(5, 123, 7, Celestials.Planet) },
					FarmState.AttackSent,
					new EspionageReport {
						ID = 101,
						Date = observedAt,
						Coordinate = new Coordinate(5, 123, 7, Celestials.Planet),
						Metal = 1000,
						Crystal = 500,
						Deuterium = 250
					});

				using (var store = new AutoFarmStateStore("autofarm_dashboard-test.db", dataFolder)) {
					store.SaveSystemSnapshot(5, 123, observedAt, false, new List<Planet>());
					store.UpsertTarget(target, observedAt);
					store.RecordAttack(
						target,
						new Resources(metal: 100, crystal: 50, deuterium: 25),
						observedAt,
						new Coordinate(5, 120, 4, Celestials.Planet),
						Missions.Attack.ToString());
				}

				var dashboard = new AutoFarmDashboardReader().Read("dashboard-test");

				Assert.True(dashboard.DatabaseAvailable);
				Assert.Equal(1, dashboard.CachedSystemCount);
				Assert.Equal("Cached no targets", dashboard.Systems[0].Status);
				Assert.Single(dashboard.Reports);
				Assert.Equal("AttackSent", dashboard.Reports[0].State);
				Assert.Equal("Attack sent", dashboard.Reports[0].Reason);
				Assert.Single(dashboard.Attacks);
				Assert.Equal(101, dashboard.Attacks[0].ReportId);
				Assert.Equal("[P:5:120:4]", dashboard.Attacks[0].Origin);
				Assert.Equal("Attack", dashboard.Attacks[0].Mission);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public void AutoFarmDashboardOnlyExposesAutoFarmLogs() {
			var logsPath = Path.Combine(Path.GetTempPath(), "tbot-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(logsPath);
			var logFile = Path.Combine(logsPath, $"TBot{DateTime.Now:yyyyMMdd}.csv");
			var timestamp = DateTime.Now.ToString(CultureInfo.InvariantCulture);
			File.WriteAllText(logFile,
				$"type,sender,datetime,message{Environment.NewLine}" +
				$"Information,AutoFarm,{timestamp},AutoFarm decision{Environment.NewLine}" +
				$"Information,Expeditions,{timestamp},Expedition activity{Environment.NewLine}" +
				$"Error,FleetScheduler,{timestamp},Unrelated scheduler error{Environment.NewLine}");

			var previousLogsPath = SettingsService.LogsPath;
			try {
				SettingsService.LogsPath = logsPath;
				var dashboard = new AutoFarmDashboardReader().Read("log-filter-test");

				Assert.Single(dashboard.Logs);
				Assert.Equal("AutoFarm", dashboard.Logs[0].Sender);
				Assert.Empty(dashboard.Errors);
			} finally {
				SettingsService.LogsPath = previousLogsPath;
				Directory.Delete(logsPath, true);
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

		[Fact]
		public void ConstructionPlannerOrdersTheBestReturnFirstAndIgnoresIneligibleCandidates() {
			var ordered = AccountConstructionPlanner.Order(new[] {
				new ConstructionCandidate { CelestialId = 2, Priority = 0, Score = 4 },
				new ConstructionCandidate { CelestialId = 1, Priority = 0, Score = 2 },
				new ConstructionCandidate { CelestialId = 3, Priority = 0, Score = 1, Eligible = false }
			});

			Assert.Equal(new[] { 1, 2 }, ordered.Select(candidate => candidate.CelestialId));
		}

		[Fact]
		public void ConstructionPlannerPrioritizesUrgentAutoMineWorkBeforeMines() {
			Assert.True(AccountConstructionPlanner.GetAutoMinePriority(Buildables.FusionReactor) < AccountConstructionPlanner.GetAutoMinePriority(Buildables.MetalMine));
			Assert.True(AccountConstructionPlanner.GetAutoMinePriority(Buildables.Terraformer) < AccountConstructionPlanner.GetAutoMinePriority(Buildables.FusionReactor));
			Assert.True(AccountConstructionPlanner.GetAutoMinePriority(Buildables.MetalMine) < AccountConstructionPlanner.GetAutoMinePriority(Buildables.Null));
		}

		[Fact]
		public void ConstructionPlannerOrdersCelestialsByTheirBestCandidate() {
			var celestials = new[] {
				new Planet { ID = 3 },
				new Planet { ID = 1 },
				new Planet { ID = 2 }
			};
			var ordered = AccountConstructionPlanner.OrderCelestials(celestials, new[] {
				new ConstructionCandidate { CelestialId = 1, Priority = 0, Score = 5 },
				new ConstructionCandidate { CelestialId = 1, Priority = 0, Score = 3 },
				new ConstructionCandidate { CelestialId = 2, Priority = 0, Score = 1 }
			});

			Assert.Equal(new[] { 2, 1, 3 }, ordered.Select(celestial => celestial.ID));
		}

		[Fact]
		public void ResourcePlannerMergesDemandsForTheSameDestinationIntoOneShipment() {
			var plan = ResourceShipmentPlanner.Plan(
				new[] {
					new ResourceDemand(20, new Resources(metal: 100, crystal: 50), 0, "mine"),
					new ResourceDemand(20, new Resources(metal: 50, deuterium: 25), 1, "lifeform")
				},
				new[] { new ResourceSource(10, new Resources(metal: 150, crystal: 50, deuterium: 25), 225) });

			Assert.True(plan.IsComplete);
			Assert.Single(plan.Shipments);
			Assert.Equal(225, plan.Shipments[0].Amount.TotalResources);
			Assert.Equal(150, plan.Shipments[0].Amount.Metal);
		}

		[Fact]
		public void ResourcePlannerNeverReusesResourcesAcrossDestinations() {
			var plan = ResourceShipmentPlanner.Plan(
				new[] {
					new ResourceDemand(20, new Resources(metal: 100), 0),
					new ResourceDemand(30, new Resources(metal: 100), 1)
				},
				new[] { new ResourceSource(10, new Resources(metal: 150), 150) });

			Assert.Single(plan.Shipments);
			Assert.Single(plan.UnfulfilledDemands);
			Assert.Equal(20, plan.Shipments[0].DestinationId);
			Assert.Equal(100, plan.Shipments[0].Amount.Metal);
		}

		[Fact]
		public void ResourcePlannerDoesNotCommitPartialDemandWhenTheBatchCannotFundIt() {
			var plan = ResourceShipmentPlanner.Plan(
				new[] { new ResourceDemand(20, new Resources(metal: 100, crystal: 100)) },
				new[] { new ResourceSource(10, new Resources(metal: 100), 100) });

			Assert.False(plan.IsComplete);
			Assert.Empty(plan.Shipments);
			Assert.Single(plan.UnfulfilledDemands);
		}

		[Fact]
		public void ResourcePlannerIgnoresNegativeResourceAmounts() {
			var plan = ResourceShipmentPlanner.Plan(
				new[] { new ResourceDemand(20, new Resources(metal: -100, crystal: 50, deuterium: -10)) },
				new[] { new ResourceSource(10, new Resources(crystal: 50), 50) });

			Assert.True(plan.IsComplete);
			Assert.Single(plan.Shipments);
			Assert.Equal(50, plan.Shipments[0].Amount.Crystal);
			Assert.Equal(0, plan.Shipments[0].Amount.Metal);
			Assert.Equal(0, plan.Shipments[0].Amount.Deuterium);
		}

		[Fact]
		public void ResourcePlannerDoesNotExceedTheShipmentBudget() {
			var plan = ResourceShipmentPlanner.Plan(
				new[] { new ResourceDemand(20, new Resources(metal: 100)) },
				new[] {
					new ResourceSource(10, new Resources(metal: 50), 50),
					new ResourceSource(11, new Resources(metal: 50), 50)
				},
				maxShipments: 1);

			Assert.False(plan.IsComplete);
			Assert.Empty(plan.Shipments);
			Assert.Single(plan.UnfulfilledDemands);
		}

		[Fact]
		public void ResourcePlannerSplitsOneDemandAcrossOriginsWithoutLosingResources() {
			var plan = ResourceShipmentPlanner.Plan(
				new[] { new ResourceDemand(20, new Resources(metal: 300)) },
				new[] {
					new ResourceSource(10, new Resources(metal: 150), 150),
					new ResourceSource(11, new Resources(metal: 150), 150)
				});

			Assert.True(plan.IsComplete);
			Assert.Equal(2, plan.Shipments.Count);
			Assert.Equal(300, plan.Shipments.Sum(shipment => shipment.Amount.TotalResources));
		}

		[Fact]
		public void ResourcePlannerDoesNotConsumeSubthresholdSimulationCapacity() {
			var plan = ResourceShipmentPlanner.Plan(
				new[] {
					new ResourceDemand(20, new Resources(metal: 30, crystal: 70), 0),
					new ResourceDemand(21, new Resources(metal: 150), 1)
				},
				new[] {
					new ResourceSource(1, new Resources(metal: 100), 100),
					new ResourceSource(2, new Resources(metal: 30, crystal: 70), 100),
					new ResourceSource(3, new Resources(metal: 50), 50)
				},
				minimumShipmentResources: 50);

			Assert.True(plan.IsComplete);
			Assert.Equal(3, plan.Shipments.Count);
			Assert.Equal(100, plan.Shipments.Single(shipment => shipment.OriginId == 1 && shipment.DestinationId == 21).Amount.Metal);
			Assert.Equal(50, plan.Shipments.Single(shipment => shipment.OriginId == 3 && shipment.DestinationId == 21).Amount.Metal);
		}

		[Fact]
		public void AutoFarmDashboardReaderReadsLegacyAttackHistoryWithoutOriginColumns() {
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			Directory.CreateDirectory(dataFolder);
			var databasePath = Path.Combine(dataFolder, "autofarm_legacy-dashboard.db");

			try {
				if (File.Exists(databasePath))
					File.Delete(databasePath);

				SQLitePCL.Batteries_V2.Init();
				using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False")) {
					connection.Open();
					using var command = connection.CreateCommand();
					command.CommandText = @"
CREATE TABLE systems (galaxy INTEGER, system INTEGER, observed_at_utc TEXT, is_empty INTEGER, planets_json TEXT);
CREATE TABLE targets (galaxy INTEGER, system INTEGER, position INTEGER, celestial_type INTEGER, name TEXT, state INTEGER, report_json TEXT, updated_at_utc TEXT);
CREATE TABLE attacks (report_id INTEGER PRIMARY KEY, galaxy INTEGER, system INTEGER, position INTEGER, celestial_type INTEGER, target_name TEXT, dispatched_at_utc TEXT, metal INTEGER, crystal INTEGER, deuterium INTEGER);
CREATE TABLE scan_cursor (id INTEGER PRIMARY KEY, range_index INTEGER, galaxy INTEGER, system INTEGER, updated_at_utc TEXT);
INSERT INTO systems VALUES (1, 139, '2026-08-10T12:00:00.0000000Z', 1, '[]');
INSERT INTO attacks VALUES (77, 1, 139, 7, 1, 'Legacy target', '2026-08-10T12:01:00.0000000Z', 100, 50, 25);";
					command.ExecuteNonQuery();
				}

				var dashboard = new AutoFarmDashboardReader().Read("legacy-dashboard");

				Assert.True(dashboard.DatabaseAvailable);
				Assert.Single(dashboard.Attacks);
				Assert.Equal("Unknown origin", dashboard.Attacks[0].Origin);
				Assert.Equal("Attack", dashboard.Attacks[0].Mission);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}
	}
}
