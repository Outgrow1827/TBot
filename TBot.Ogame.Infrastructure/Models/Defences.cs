using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class Defences {
		public long RocketLauncher { get; set; }
		public long LightLaser { get; set; }
		public long HeavyLaser { get; set; }
		public long GaussCannon { get; set; }
		public long IonCannon { get; set; }
		public long PlasmaTurret { get; set; }
		public long SmallShieldDome { get; set; }
		public long LargeShieldDome { get; set; }
		public long AntiBallisticMissiles { get; set; }
		public long InterplanetaryMissiles { get; set; }

		// Built once via reflection instead of on every GetAmount() call.
		private static readonly Dictionary<Buildables, Func<Defences, long>> _accessors = BuildAccessors();

		private static Dictionary<Buildables, Func<Defences, long>> BuildAccessors() {
			var map = new Dictionary<Buildables, Func<Defences, long>>();
			foreach (PropertyInfo prop in typeof(Defences).GetProperties()) {
				if (prop.PropertyType != typeof(long) || !Enum.TryParse<Buildables>(prop.Name, out var buildable))
					continue;
				var instance = Expression.Parameter(typeof(Defences), "instance");
				map[buildable] = Expression.Lambda<Func<Defences, long>>(Expression.Property(instance, prop), instance).Compile();
			}
			return map;
		}

		public int GetAmount(Buildables defence) {
			return _accessors.TryGetValue(defence, out var getter) ? (int) getter(this) : 0;
		}
	}

}
