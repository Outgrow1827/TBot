using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class Facilities {
		public int RoboticsFactory { get; set; }
		public int Shipyard { get; set; }
		public int ResearchLab { get; set; }
		public int AllianceDepot { get; set; }
		public int MissileSilo { get; set; }
		public int NaniteFactory { get; set; }
		public int Terraformer { get; set; }
		public int SpaceDock { get; set; }
		public int LunarBase { get; set; }
		public int SensorPhalanx { get; set; }
		public int JumpGate { get; set; }

		public override string ToString() {
			return $"R: {RoboticsFactory.ToString()} S: {Shipyard.ToString()} L: {ResearchLab.ToString()} M: {MissileSilo.ToString("")} N: {NaniteFactory.ToString("")}";
		}

		// Built once via reflection instead of on every GetLevel()/SetLevel() call.
		private static readonly Dictionary<Buildables, (Func<Facilities, int> Get, Action<Facilities, int> Set)> _accessors = BuildAccessors();

		private static Dictionary<Buildables, (Func<Facilities, int>, Action<Facilities, int>)> BuildAccessors() {
			var map = new Dictionary<Buildables, (Func<Facilities, int>, Action<Facilities, int>)>();
			foreach (PropertyInfo prop in typeof(Facilities).GetProperties()) {
				if (prop.PropertyType != typeof(int) || !Enum.TryParse<Buildables>(prop.Name, out var building))
					continue;

				var instance = Expression.Parameter(typeof(Facilities), "instance");
				var getter = Expression.Lambda<Func<Facilities, int>>(Expression.Property(instance, prop), instance).Compile();

				var value = Expression.Parameter(typeof(int), "value");
				var setter = Expression.Lambda<Action<Facilities, int>>(Expression.Assign(Expression.Property(instance, prop), value), instance, value).Compile();

				map[building] = (getter, setter);
			}
			return map;
		}

		public int GetLevel(Buildables building) {
			return _accessors.TryGetValue(building, out var accessor) ? accessor.Get(this) : 0;
		}

		public Facilities SetLevel(Buildables building, int level) {
			if (_accessors.TryGetValue(building, out var accessor))
				accessor.Set(this, level);
			return this;
		}
	}

}
