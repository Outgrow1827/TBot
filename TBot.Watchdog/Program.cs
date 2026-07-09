using System.Diagnostics;
using System.Text.Json;

namespace TBot.Watchdog;

// Companion process to TBot.exe: catches whole-process hangs/crashes that nothing running inside
// TBot.exe itself could ever detect (see TBot/Workers/WatchdogWorker.cs for the internal, per-worker
// equivalent - that one restarts a single stuck worker, this one restarts the whole bot).
//
// Started by TBot.exe at boot (Program.TryStartExternalWatchdog), passed the PID to watch via
// --watch-pid. Watches:
//   1. whether that process is still running at all;
//   2. if it exits, whether it exited cleanly (code 0 - the user stopped it on purpose, e.g. Ctrl+C)
//      or not (crash - restart it);
//   3. while it's running, whether TBot's own internal watchdog is still writing heartbeat files
//      (data\watchdog_heartbeat_{alias}.txt) - if those go stale, the process is hung even though
//      Windows still sees it as "running", so it gets killed and restarted too.
//
// On a clean exit (code 0) this process exits too - no bot to watch, no reason to keep relaunching it
// behind the user's back just because they closed it.
internal class Program {
	private static async Task<int> Main(string[] args) {
		string exeDir = AppContext.BaseDirectory;
		string logPath = Path.Combine(exeDir, "watchdog_external.log");

		int? watchPid = null;
		foreach (var arg in args) {
			if (arg.StartsWith("--watch-pid=") && int.TryParse(arg["--watch-pid=".Length..], out var pid)) {
				watchPid = pid;
			}
		}
		if (watchPid == null) {
			Log(logPath, "No --watch-pid provided, nothing to watch. Exiting.");
			return 1;
		}

		var config = LoadConfig(exeDir, logPath);
		if (!config.Active) {
			Log(logPath, "ExternalWatchdog.Active is false in settings.json, exiting.");
			return 0;
		}

		Log(logPath, $"Watching PID {watchPid.Value}. MaxHeartbeatAgeMinutes={config.MaxHeartbeatAgeMinutes}, PollIntervalSeconds={config.PollIntervalSeconds}, StartupGraceMinutes={config.StartupGraceMinutes}.");

		Process? proc = TryGetProcess(watchPid.Value);
		DateTime lastRestart = DateTime.UtcNow;

		while (true) {
			try {
				if (proc == null || proc.HasExited) {
					int exitCode = proc?.HasExited == true ? SafeExitCode(proc) : -1;
					if (exitCode == 0) {
						Log(logPath, "TBot.exe exited cleanly (code 0) - assuming intentional shutdown. Watchdog exiting too.");
						return 0;
					}

					Log(logPath, $"TBot.exe is not running (last exit code: {exitCode}). Restarting...");
					await AlertTelegram(config, $"🚨 Watchdog externo: TBot.exe não está rodando (código de saída: {exitCode}). Reiniciando...");
					proc = StartTBot(exeDir, logPath);
					lastRestart = DateTime.UtcNow;
					if (proc == null) {
						// Couldn't even start it - back off before retrying so we don't spin a tight loop.
						await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, config.PollIntervalSeconds)));
					}
					continue;
				}

				bool exitedDuringWait = proc.WaitForExit((int) TimeSpan.FromSeconds(config.PollIntervalSeconds).TotalMilliseconds);
				if (exitedDuringWait) {
					continue; // handled at the top of the next loop iteration
				}

				bool withinGrace = DateTime.UtcNow - lastRestart < TimeSpan.FromMinutes(config.StartupGraceMinutes);
				if (withinGrace) {
					continue; // still warming up, give TBot time to log in and get its first watchdog tick
				}

				var (isStale, staleness) = CheckHeartbeatStale(exeDir, config);
				if (isStale) {
					Log(logPath, $"Heartbeat stale for {staleness}. TBot.exe (PID {proc.Id}) looks hung - killing and restarting.");
					await AlertTelegram(config, $"🚨 Watchdog externo: TBot.exe parece travado (heartbeat parado há {staleness}). Matando e reiniciando o processo.");
					TryKill(proc, logPath);
					proc = StartTBot(exeDir, logPath);
					lastRestart = DateTime.UtcNow;
				}
			} catch (Exception e) {
				// Never let this loop die - that would silently disable the whole safety net.
				Log(logPath, $"Unexpected error in watchdog loop: {e}");
				await Task.Delay(TimeSpan.FromSeconds(30));
			}
		}
	}

	private record Config(
		bool Active,
		int MaxHeartbeatAgeMinutes,
		int PollIntervalSeconds,
		int StartupGraceMinutes,
		string[] InstanceAliases,
		string? TelegramApi,
		string? TelegramChatId,
		bool TelegramActive
	);

	private static Config LoadConfig(string exeDir, string logPath) {
		string settingsPath = Path.Combine(exeDir, "settings.json");
		bool active = true;
		int maxHeartbeatAgeMinutes = 10;
		int pollIntervalSeconds = 60;
		int startupGraceMinutes = 5;
		var aliases = new List<string>();
		string? telegramApi = null;
		string? telegramChatId = null;
		bool telegramActive = false;

		try {
			using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
			var root = doc.RootElement;

			if (root.TryGetProperty("ExternalWatchdog", out var ew)) {
				if (ew.TryGetProperty("Active", out var a)) active = a.GetBoolean();
				if (ew.TryGetProperty("MaxHeartbeatAgeMinutes", out var m)) maxHeartbeatAgeMinutes = m.GetInt32();
				if (ew.TryGetProperty("PollIntervalSeconds", out var p)) pollIntervalSeconds = p.GetInt32();
				if (ew.TryGetProperty("StartupGraceMinutes", out var s)) startupGraceMinutes = s.GetInt32();
			}

			if (root.TryGetProperty("Instances", out var instances) && instances.ValueKind == JsonValueKind.Array) {
				foreach (var inst in instances.EnumerateArray()) {
					if (inst.TryGetProperty("Alias", out var alias)) {
						aliases.Add(alias.GetString() ?? "Instance");
					}
				}
			}
			if (aliases.Count == 0) aliases.Add("Instance"); // matches TBot's own default alias

			if (root.TryGetProperty("TelegramMessenger", out var tg)) {
				if (tg.TryGetProperty("Active", out var ta)) telegramActive = ta.GetBoolean();
				if (tg.TryGetProperty("API", out var api)) telegramApi = api.GetString();
				if (tg.TryGetProperty("ChatId", out var chatId)) telegramChatId = chatId.GetString();
			}
		} catch (Exception e) {
			Log(logPath, $"Could not read settings.json ({e.Message}), using defaults (Active=true).");
		}

		return new Config(active, maxHeartbeatAgeMinutes, pollIntervalSeconds, startupGraceMinutes, aliases.ToArray(), telegramApi, telegramChatId, telegramActive);
	}

	private static (bool isStale, TimeSpan staleness) CheckHeartbeatStale(string exeDir, Config config) {
		string dataDir = Path.Combine(exeDir, "data");
		DateTime? mostRecent = null;
		bool anyFound = false;

		foreach (var alias in config.InstanceAliases) {
			string path = Path.Combine(dataDir, $"watchdog_heartbeat_{alias}.txt");
			if (!File.Exists(path)) continue;
			anyFound = true;
			var written = File.GetLastWriteTimeUtc(path);
			if (mostRecent == null || written > mostRecent) mostRecent = written;
		}

		if (!anyFound) {
			// No heartbeat files yet (still starting up, or internal Watchdog.Active=false for every
			// instance) - nothing to judge staleness against, don't false-positive on this alone.
			return (false, TimeSpan.Zero);
		}

		var age = DateTime.UtcNow - mostRecent!.Value;
		return (age > TimeSpan.FromMinutes(config.MaxHeartbeatAgeMinutes), age);
	}

	private static Process? StartTBot(string exeDir, string logPath) {
		try {
			var psi = new ProcessStartInfo {
				FileName = Path.Combine(exeDir, "TBot.exe"),
				Arguments = "--no-watchdog",
				WorkingDirectory = exeDir,
				UseShellExecute = false,
			};
			var p = Process.Start(psi);
			Log(logPath, p != null ? $"Started TBot.exe, new PID {p.Id}." : "Process.Start returned null.");
			return p;
		} catch (Exception e) {
			Log(logPath, $"Failed to start TBot.exe: {e.Message}");
			return null;
		}
	}

	private static void TryKill(Process proc, string logPath) {
		try {
			if (!proc.HasExited) {
				proc.Kill(entireProcessTree: true);
				proc.WaitForExit(10000);
			}
		} catch (Exception e) {
			Log(logPath, $"Failed to kill hung TBot.exe (PID {proc.Id}): {e.Message}");
		}
	}

	private static Process? TryGetProcess(int pid) {
		try {
			return Process.GetProcessById(pid);
		} catch {
			return null;
		}
	}

	private static int SafeExitCode(Process proc) {
		try {
			return proc.ExitCode;
		} catch {
			return -1;
		}
	}

	private static async Task AlertTelegram(Config config, string message) {
		if (!config.TelegramActive || string.IsNullOrWhiteSpace(config.TelegramApi) || string.IsNullOrWhiteSpace(config.TelegramChatId)) {
			return;
		}
		try {
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
			string url = $"https://api.telegram.org/bot{config.TelegramApi}/sendMessage";
			var content = new FormUrlEncodedContent(new Dictionary<string, string> {
				["chat_id"] = config.TelegramChatId,
				["text"] = message,
			});
			await client.PostAsync(url, content);
		} catch {
			// Best-effort only - a failed Telegram alert shouldn't stop the watchdog from doing its job.
		}
	}

	private static void Log(string logPath, string message) {
		string line = $"[{DateTime.UtcNow:O}] {message}";
		Console.WriteLine(line);
		try {
			File.AppendAllText(logPath, line + Environment.NewLine);
		} catch {
			// If we can't write the log file, there's nothing useful to do about it here.
		}
	}
}
