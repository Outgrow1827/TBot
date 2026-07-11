using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class Researches {
		public int EnergyTechnology { get; set; }
		public int LaserTechnology { get; set; }
		public int IonTechnology { get; set; }
		public int HyperspaceTechnology { get; set; }
		public int PlasmaTechnology { get; set; }
		public int CombustionDrive { get; set; }
		public int ImpulseDrive { get; set; }
		public int HyperspaceDrive { get; set; }
		public int EspionageTechnology { get; set; }
		public int ComputerTechnology { get; set; }
		public int Astrophysics { get; set; }
		public int IntergalacticResearchNetwork { get; set; }
		public int GravitonTechnology { get; set; }
		public int WeaponsTechnology { get; set; }
		public int ShieldingTechnology { get; set; }
		public int ArmourTechnology { get; set; }

		// Built once via reflection instead of on every GetLevel() call.
		private static readonly Dictionary<Buildables, Func<Researches, int>> _accessors = BuildAccessors();

		private static Dictionary<Buildables, Func<Researches, int>> BuildAccessors() {
			var map = new Dictionary<Buildables, Func<Researches, int>>();
			foreach (PropertyInfo prop in typeof(Researches).GetProperties()) {
				if (prop.PropertyType != typeof(int) || !Enum.TryParse<Buildables>(prop.Name, out var research))
					continue;
				var instance = Expression.Parameter(typeof(Researches), "instance");
				map[research] = Expression.Lambda<Func<Researches, int>>(Expression.Property(instance, prop), instance).Compile();
			}
			return map;
		}

		public int GetLevel(Buildables research) {
			return _accessors.TryGetValue(research, out var getter) ? getter(this) : 0;
		}
	}

}
