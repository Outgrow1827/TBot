using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class Buildings {
		public int MetalMine { get; set; }
		public int CrystalMine { get; set; }
		public int DeuteriumSynthesizer { get; set; }
		public int SolarPlant { get; set; }
		public int FusionReactor { get; set; }
		public int SolarSatellite { get; set; }
		public int MetalStorage { get; set; }
		public int CrystalStorage { get; set; }
		public int DeuteriumTank { get; set; }

		public override string ToString() {
			return $"M: {MetalMine.ToString()} C: {CrystalMine.ToString()} D: {DeuteriumSynthesizer.ToString()} S: {SolarPlant.ToString("")} F: {FusionReactor.ToString("")}";
		}

		// Built once from reflection at class-init time instead of on every GetLevel()/SetLevel() call -
		// see Ships.cs for the same pattern applied to the (much hotter) ship-count accessors.
		private static readonly Dictionary<Buildables, (Func<Buildings, int> Get, Action<Buildings, int> Set)> _accessors = BuildAccessors();

		private static Dictionary<Buildables, (Func<Buildings, int>, Action<Buildings, int>)> BuildAccessors() {
			var map = new Dictionary<Buildables, (Func<Buildings, int>, Action<Buildings, int>)>();
			foreach (PropertyInfo prop in typeof(Buildings).GetProperties()) {
				if (prop.PropertyType != typeof(int) || !Enum.TryParse<Buildables>(prop.Name, out var building))
					continue;

				var instance = Expression.Parameter(typeof(Buildings), "instance");
				var getter = Expression.Lambda<Func<Buildings, int>>(Expression.Property(instance, prop), instance).Compile();

				var value = Expression.Parameter(typeof(int), "value");
				var setter = Expression.Lambda<Action<Buildings, int>>(Expression.Assign(Expression.Property(instance, prop), value), instance, value).Compile();

				map[building] = (getter, setter);
			}
			return map;
		}

		public int GetLevel(Buildables building) {
			return _accessors.TryGetValue(building, out var accessor) ? accessor.Get(this) : 0;
		}

		public Buildings SetLevel(Buildables buildable, int level) {
			if (_accessors.TryGetValue(buildable, out var accessor))
				accessor.Set(this, level);
			return this;
		}
	}

}
