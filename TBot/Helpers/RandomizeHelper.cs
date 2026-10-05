using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Model;

namespace Tbot.Helpers {
	public static class RandomizeHelper {
		private static readonly Random _rand = new();
		private static readonly object _lock = new object();

		private static int Next(int min, int max) {
			lock (_lock) { return _rand.Next(min, max); }
		}

		public static int CalcRandomInterval(IntervalType type) {
			return type switch {
				IntervalType.LessThanASecond => Next(500, 1000),
				IntervalType.LessThanFiveSeconds => Next(1000, 5000),
				IntervalType.AFewSeconds => Next(5000, 15000),
				IntervalType.SomeSeconds => Next(20000, 50000),
				IntervalType.AMinuteOrTwo => Next(40000, 140000),
				IntervalType.AboutFiveMinutes => Next(240000, 360000),
				IntervalType.AboutTenMinutes => Next(540000, 720000),
				IntervalType.AboutAQuarterHour => Next(840000, 960000),
				IntervalType.AboutHalfAnHour => Next(1500000, 2100000),
				IntervalType.AboutAnHour => Next(3000000, 4200000),
				_ => Next(500, 1000),
			};
		}

		public static int CalcRandomInterval(int min, int max) {
			var minMillis = min * 60 * 1000;
			var maxMillis = max * 60 * 1000;
			return Next(minMillis, maxMillis);
		}

		public static int CalcRandomIntervalSecToMs(int min, int max) {
			var minMillis = min * 1000;
			var maxMillis = max * 1000;
			return Next(minMillis, maxMillis);
		}
	}
}
