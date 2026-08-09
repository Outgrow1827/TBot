using System;
using System.Collections.Generic;
using System.Linq;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Includes {
	/// <summary>
	/// A single construction decision produced while observing one celestial.
	/// Lower priority and score values are preferred.
	/// </summary>
	public sealed class ConstructionCandidate {
		public int CelestialId { get; init; }
		public int Priority { get; init; }
		public double Score { get; init; }
		public bool Eligible { get; init; } = true;
	}

	/// <summary>
	/// Keeps construction ordering deterministic. Workers still own the OGame
	/// action; this class only decides which observed candidates go first.
	/// </summary>
	public static class AccountConstructionPlanner {
		public static int GetAutoMinePriority(Buildables buildable) {
			return buildable switch {
				Buildables.Terraformer => 0,
				Buildables.SolarPlant or Buildables.FusionReactor => 1,
				Buildables.Crawler or Buildables.MetalStorage or Buildables.CrystalStorage or Buildables.DeuteriumTank => 2,
				Buildables.RoboticsFactory or Buildables.NaniteFactory or Buildables.Shipyard or Buildables.ResearchLab or Buildables.MissileSilo or Buildables.SpaceDock => 3,
				Buildables.MetalMine or Buildables.CrystalMine or Buildables.DeuteriumSynthesizer => 4,
				_ => 5
			};
		}

		public static IReadOnlyList<ConstructionCandidate> Order(IEnumerable<ConstructionCandidate> candidates) {
			if (candidates == null)
				return Array.Empty<ConstructionCandidate>();

			return candidates
				.Where(candidate => candidate != null && candidate.Eligible)
				.OrderBy(candidate => candidate.Priority)
				.ThenBy(candidate => NormalizeScore(candidate.Score))
				.ThenBy(candidate => candidate.CelestialId)
				.ToList();
		}

		public static IReadOnlyList<Celestial> OrderCelestials(
			IEnumerable<Celestial> celestials,
			IEnumerable<ConstructionCandidate> candidates) {
			var candidateByCelestial = Order(candidates)
				.GroupBy(candidate => candidate.CelestialId)
				.ToDictionary(group => group.Key, group => group.First());

			return (celestials ?? Enumerable.Empty<Celestial>())
				.Where(celestial => celestial != null)
				.OrderBy(celestial => candidateByCelestial.TryGetValue(celestial.ID, out var candidate) ? candidate.Priority : int.MaxValue)
				.ThenBy(celestial => candidateByCelestial.TryGetValue(celestial.ID, out var candidate) ? NormalizeScore(candidate.Score) : double.MaxValue)
				.ThenBy(celestial => celestial.ID)
				.ToList();
		}

		private static double NormalizeScore(double score) {
			return double.IsNaN(score) || double.IsInfinity(score) ? double.MaxValue : score;
		}
	}

	/// <summary>
	/// Keeps an ordered batch of celestial workers ordered while retaining the
	/// small randomized delay used by the workers for natural-looking activity.
	/// </summary>
	public static class OrderedWorkerSchedule {
		public static TimeSpan NextDueTime(TimeSpan previousDueTime, TimeSpan randomizedGap) {
			return previousDueTime + (randomizedGap < TimeSpan.Zero ? TimeSpan.Zero : randomizedGap);
		}
	}

	public sealed class ResourceDemand {
		public ResourceDemand(int destinationId, Resources amount, int priority = 0, string description = null) {
			DestinationId = destinationId;
			Amount = amount ?? new();
			Priority = priority;
			Description = description;
		}

		public int DestinationId { get; }
		public Resources Amount { get; }
		public int Priority { get; }
		public string Description { get; }
	}

	public sealed class ResourceSource {
		public ResourceSource(int originId, Resources available, long capacity, double distance = 0, double selectionScore = 0) {
			OriginId = originId;
			Available = available ?? new();
			Capacity = Math.Max(0, capacity);
			Distance = distance;
			SelectionScore = selectionScore;
		}

		public int OriginId { get; }
		public Resources Available { get; set; }
		public long Capacity { get; set; }
		public double Distance { get; }
		public double SelectionScore { get; }
	}

	public sealed class PlannedResourceShipment {
		public PlannedResourceShipment(int originId, int destinationId, Resources amount) {
			OriginId = originId;
			DestinationId = destinationId;
			Amount = amount ?? new();
		}

		public int OriginId { get; }
		public int DestinationId { get; }
		public Resources Amount { get; }
	}

	public sealed class ResourceShipmentPlan {
		public ResourceShipmentPlan(IReadOnlyList<PlannedResourceShipment> shipments, IReadOnlyList<ResourceDemand> unfulfilledDemands) {
			Shipments = shipments ?? Array.Empty<PlannedResourceShipment>();
			UnfulfilledDemands = unfulfilledDemands ?? Array.Empty<ResourceDemand>();
		}

		public IReadOnlyList<PlannedResourceShipment> Shipments { get; }
		public IReadOnlyList<ResourceDemand> UnfulfilledDemands { get; }
		public bool IsComplete => UnfulfilledDemands.Count == 0;
	}

	/// <summary>
	/// Allocates account resources once for a batch of construction demands.
	/// Demands for the same destination are merged before source selection.
	/// A demand is committed only when its complete cost can be funded, so the
	/// planner never sends a partial fleet that cannot unlock the construction.
	/// </summary>
	public static class ResourceShipmentPlanner {
		public static ResourceShipmentPlan Plan(
			IEnumerable<ResourceDemand> demands,
			IEnumerable<ResourceSource> sources,
			int maxShipments = int.MaxValue,
			long minimumShipmentResources = 0) {
			var groupedDemands = (demands ?? Enumerable.Empty<ResourceDemand>())
				.Where(demand => demand != null && demand.Amount != null)
				.Select(demand => new ResourceDemand(demand.DestinationId, Normalize(demand.Amount), demand.Priority, demand.Description))
				.Where(demand => demand.Amount.TotalResources > 0)
				.GroupBy(demand => demand.DestinationId)
				.Select(group => new ResourceDemand(
					group.Key,
					group.Aggregate(new Resources(), (total, demand) => total.Sum(demand.Amount)),
					group.Min(demand => demand.Priority),
					string.Join("; ", group.Select(demand => demand.Description).Where(description => !string.IsNullOrWhiteSpace(description)))))
				.OrderBy(demand => demand.Priority)
				.ThenBy(demand => demand.DestinationId)
				.ToList();

			var availableSources = (sources ?? Enumerable.Empty<ResourceSource>())
				.Where(source => source != null && source.Available != null && source.Available.TotalResources > 0 && source.Capacity > 0)
				.Select(source => new ResourceSource(source.OriginId, Copy(source.Available), source.Capacity, source.Distance, source.SelectionScore))
				.OrderBy(source => source.SelectionScore)
				.ThenBy(source => source.Distance)
				.ThenBy(source => source.OriginId)
				.ToList();

			var shipments = new List<PlannedResourceShipment>();
			var unfulfilled = new List<ResourceDemand>();
			var remainingShipments = Math.Max(0, maxShipments);
			minimumShipmentResources = Math.Max(0, minimumShipmentResources);

			foreach (var demand in groupedDemands) {
				var simulationSources = availableSources
					.Select(source => new ResourceSource(source.OriginId, Copy(source.Available), source.Capacity, source.Distance, source.SelectionScore))
					.ToList();
				var allocations = Allocate(demand, simulationSources, remainingShipments, minimumShipmentResources);

				if (allocations == null) {
					unfulfilled.Add(demand);
					continue;
				}

				shipments.AddRange(allocations);
				remainingShipments -= allocations.Count;
				for (var index = 0; index < availableSources.Count; index++)
					availableSources[index].Available = simulationSources[index].Available;
				for (var index = 0; index < availableSources.Count; index++)
					availableSources[index].Capacity = simulationSources[index].Capacity;
			}

			return new ResourceShipmentPlan(shipments, unfulfilled);
		}

		private static List<PlannedResourceShipment> Allocate(
			ResourceDemand demand,
			IReadOnlyList<ResourceSource> sources,
			int remainingShipments,
			long minimumShipmentResources) {
			var remaining = Copy(demand.Amount);
			var allocations = new List<PlannedResourceShipment>();

			foreach (var source in sources) {
				if (allocations.Count >= remainingShipments)
					break;
				if (source.OriginId == demand.DestinationId)
					continue;

				var amount = CalculateTake(source, remaining);
				if (amount.IsEmpty() || amount.TotalResources < minimumShipmentResources)
					continue;

				Commit(source, amount);
				allocations.Add(new PlannedResourceShipment(source.OriginId, demand.DestinationId, amount));
				remaining = Subtract(remaining, amount);
				if (remaining.IsEmpty())
					break;
			}

			return remaining.IsEmpty() ? allocations : null;
		}

		private static Resources CalculateTake(ResourceSource source, Resources remaining) {
			var capacity = Math.Min(source.Capacity, source.Available.TotalResources);
			if (capacity <= 0)
				return new();

			var amount = new Resources {
				Metal = Math.Min(remaining.Metal, Math.Min(source.Available.Metal, capacity))
			};
			capacity -= amount.Metal;
			amount.Crystal = Math.Min(remaining.Crystal, Math.Min(source.Available.Crystal, capacity));
			capacity -= amount.Crystal;
			amount.Deuterium = Math.Min(remaining.Deuterium, Math.Min(source.Available.Deuterium, capacity));
			return amount;
		}

		private static void Commit(ResourceSource source, Resources amount) {
			var taken = amount.TotalResources;
			source.Available = Subtract(source.Available, amount);
			source.Capacity -= taken;
		}

		private static Resources Subtract(Resources left, Resources right) {
			return new Resources(
				Math.Max(0, left.Metal - right.Metal),
				Math.Max(0, left.Crystal - right.Crystal),
				Math.Max(0, left.Deuterium - right.Deuterium),
				left.Energy,
				Math.Max(0, left.Food - right.Food),
				Math.Max(0, left.Population - right.Population),
				left.Darkmatter);
		}

		private static Resources Copy(Resources resources) {
			return new Resources(resources.Metal, resources.Crystal, resources.Deuterium, resources.Energy, resources.Food, resources.Population, resources.Darkmatter);
		}

		private static Resources Normalize(Resources resources) {
			return new Resources(
				Math.Max(0, resources.Metal),
				Math.Max(0, resources.Crystal),
				Math.Max(0, resources.Deuterium));
		}
	}
}
