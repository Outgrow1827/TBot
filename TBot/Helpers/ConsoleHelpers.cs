using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tbot.Helpers {
	public static class ConsoleHelpers {
		public static void SetTitle(string content = "") {
			var assembly = Assembly.GetExecutingAssembly();
			AssemblyName exeInfo = assembly.GetName();
			// informational version carries the beta label (3.5.0.1.beta11); fall back to the numeric version
			string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
				?? exeInfo.Version?.ToString();
			string info = $"{exeInfo.Name} v{version}";
			Console.Title = (content != "") ? $"{content} - {info}" : info;
			return;
		}

		public static void PlayAlarm() {
			Console.Beep();
			Thread.Sleep(1000);
			Console.Beep();
			Thread.Sleep(1000);
			Console.Beep();
			return;
		}
	}
}
