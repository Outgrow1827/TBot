using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	/// <summary>Global switch, set once at startup from instance_settings.json
	/// (General.HideSensitiveDataInLogs), that controls whether Coordinate.ToString()
	/// masks galaxy/system/position. Applies everywhere a Coordinate is interpolated
	/// into a log message, without needing to touch every log call site individually.</summary>
	public static class LogPrivacy {
		public static bool HideCoordinates = false;
	}

	public class Coordinate {
		public Coordinate(int galaxy = 1, int system = 1, int position = 1, Celestials type = Celestials.Planet) {
			Galaxy = galaxy;
			System = system;
			Position = position;
			Type = type;
		}
		public int Galaxy { get; set; }
		public int System { get; set; }
		public int Position { get; set; }
		public Celestials Type { get; set; }

		public override string ToString() {
			if (LogPrivacy.HideCoordinates) {
				return $"[{GetCelestialCode()}:hidden]";
			}
			return $"[{GetCelestialCode()}:{Galaxy}:{System}:{Position}]";
		}

		static public Coordinate FromString(String arg) {
			Coordinate output = new();
			Regex re = new Regex("(\\d{1}):(\\d{1,3}):(\\d{1,2}) (moon|planet|Moon|Planet)");
			Match m = re.Match(arg);
			if (m.Success) {
				output.Galaxy = Int32.Parse(m.Groups[1].Value);
				output.System = Int32.Parse(m.Groups[2].Value);
				output.Position = Int32.Parse(m.Groups[3].Value);

				if (m.Groups[4].Value.ToLower().Contains("moon")) {
					output.Type = Celestials.Moon;
				} else {
					output.Type = Celestials.Planet;
				}
			} else {
				throw new Exception($"Invalid Coordinate from {arg}");
			}

			return output;
		}

		private string GetCelestialCode() {
			return Type switch {
				Celestials.Planet => "P",
				Celestials.Debris => "DF",
				Celestials.Moon => "M",
				Celestials.DeepSpace => "DS",
				_ => "",
			};
		}

		/// <summary>Parses the bracketed form produced by ToString() (e.g. "[P:1:2:3]", "[M:1:2:3]") -
		/// unlike FromString(), which expects a different "1:2:3 moon"-style format used elsewhere.</summary>
		static public bool TryParse(string arg, out Coordinate coordinate) {
			coordinate = null;
			if (string.IsNullOrEmpty(arg))
				return false;
			var m = Regex.Match(arg, @"\[(P|M|DF|DS):(\d+):(\d+):(\d+)\]");
			if (!m.Success)
				return false;
			Celestials type = m.Groups[1].Value switch {
				"P" => Celestials.Planet,
				"M" => Celestials.Moon,
				"DF" => Celestials.Debris,
				"DS" => Celestials.DeepSpace,
				_ => Celestials.Planet,
			};
			coordinate = new Coordinate(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), type);
			return true;
		}

		public bool IsSame(Coordinate otherCoord) {
			return Galaxy == otherCoord.Galaxy
				&& System == otherCoord.System
				&& Position == otherCoord.Position
				&& Type == otherCoord.Type;
		}
	}

}
