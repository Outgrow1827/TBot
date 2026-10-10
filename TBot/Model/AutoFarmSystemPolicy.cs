using System.Collections.Generic;
using System.Linq;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	/// <summary>
	/// Defines which galaxy-view results are useful to AutoFarm and which
	/// systems can safely receive the long empty-system cooldown.
	/// </summary>
	public static class AutoFarmSystemPolicy {
		public static bool IsEligibleTarget(Planet planet) {
			return planet != null
				&& planet.Inactive
				&& !planet.Administrator
				&& !planet.Banned
				&& !planet.Vacation;
		}

		public static bool IsVacationInactive(Planet planet) {
			return planet != null && planet.Inactive && planet.Vacation;
		}

		public static bool IsEmptySystem(IEnumerable<Planet> planets) {
			var knownPlanets = (planets ?? Enumerable.Empty<Planet>())
				.Where(planet => planet != null)
				.ToList();

			// "Empty" means the long 30-day cooldown is safe: nothing was seen, or everything seen
			// was a vacation-mode inactive planet (truly abandoned signal). A real active player,
			// or an inactive-but-ineligible one (admin/banned), means the system was genuinely
			// visited and isn't empty - it just has no farm target right now, which gets the
			// normal 7-day cooldown instead (see AutoFarmDoesNotUseTheLongCooldownForARegularInactivePlanet).
			return knownPlanets.Count == 0 || knownPlanets.All(IsVacationInactive);
		}

		public static List<Celestial> GetEligibleTargets(IEnumerable<Planet> planets) {
			return (planets ?? Enumerable.Empty<Planet>())
				.Where(IsEligibleTarget)
				.Cast<Celestial>()
				.ToList();
		}
	}
}
