using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {

	public delegate Task WorkerFunction(CancellationToken ct);

	public abstract class WorkerBase : ITBotWorker {
		protected readonly ITBotMain _tbotInstance;

		protected CancellationToken _ct = CancellationToken.None;
		protected Dictionary<string, Timer> timers = new();
		
		private SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
		private AsyncTimer _timer = null;

		// True once ExecutionWrapper has already logged "not enabled by settings" for the CURRENT
		// disabled streak - lets Start/Restart/Stop below skip their own admin-log lines too instead
		// of announcing a restart cycle that's just going to immediately no-op again.
		private bool _disabledStreakLogged = false;
		private bool ShouldLogAdminLines() => IsWorkerEnabledBySettings() || !_disabledStreakLogged;

		protected IWorkerFactory _workerFactory;
		protected ConcurrentDictionary<Celestial, ITBotCelestialWorker> _celestialWorkers = new();

		public ConcurrentDictionary<Celestial, ITBotCelestialWorker> celestialWorkers {
			get {
				return _celestialWorkers ?? new();
			}
		}

		public TimeSpan DueTime {
			get {
				return (_timer != null) ? _timer.DueTime : TimeSpan.Zero;
			}
		}
		public TimeSpan Period {
			get {
				return (_timer != null) ? _timer.Period : TimeSpan.Zero;
			}
		}

		public WorkerBase(ITBotMain parentInstance) {
			_tbotInstance = parentInstance;
		}

		protected abstract Task Execute();
		public void DoLog(LogLevel level, string format) {
			_tbotInstance.log(level, GetLogSender(), format);
		}

		public async Task StartWorker(CancellationToken ct, TimeSpan period, TimeSpan dueTime) {

			if (ShouldLogAdminLines())
				DoLog(LogLevel.Information, $"Starting Worker \"{GetWorkerName()}\"..");

			await StopWorker();

			_ct = ct;

			// TimeSpan periodSpan = TimeSpan.FromMilliseconds(RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
			// ThreadName cannot be longer than 16 bytes, so
			string cutAlias = (_tbotInstance.InstanceAlias.Length > 6 ? _tbotInstance.InstanceAlias.Substring(0, 6) : _tbotInstance.InstanceAlias);
			_timer = new AsyncTimer(ExecutionWrapper, $"{cutAlias}{GetWorkerName()}");
			await _timer.StartAsync(ct, period, dueTime);
		}
		public async Task StartWorker(CancellationToken ct, TimeSpan dueTime) {
			await StartWorker(ct, Timeout.InfiniteTimeSpan, dueTime);
		}
		public async Task StopWorker() {
			// Stop also all the timers
			RemoveAllTimers();
			if (_timer != null) {
				bool logAdmin = ShouldLogAdminLines();
				if (logAdmin)
					DoLog(LogLevel.Information, $"Closing Worker \"{GetWorkerName()}\"..");
				await _timer.DisposeAsync();
				if (logAdmin)
					DoLog(LogLevel.Information, $"Worker \"{GetWorkerName()}\" closed!");
				_timer = null;
			}
			foreach (var worker in _celestialWorkers.Values) {
				await worker.StopWorker();
			}
		}
		public void ChangeWorkerPeriod(long periodMs) {
			if (periodMs >= int.MaxValue)
				periodMs = int.MaxValue;
			ChangeWorkerPeriod(TimeSpan.FromMilliseconds(periodMs));
		}
		public void ChangeWorkerPeriod(TimeSpan period) {
			if (_timer != null)
				_timer.ChangePeriod(period);	
		}
		public void ChangeWorkerDueTime(TimeSpan dueTime) {
			if (_timer != null)
				_timer.ChangeDueTime(dueTime);
		}
		public void ChangeWorkerDueTime(long dueTimeMs) {
			if (_timer != null)
				_timer.ChangeDueTime(TimeSpan.FromMilliseconds(dueTimeMs));
		}
		public async void RestartWorker(CancellationToken ct, TimeSpan period, TimeSpan dueTime) {
			if (ShouldLogAdminLines())
				DoLog(LogLevel.Information, $"Restarting Worker \"{GetWorkerName()}\"...");
			foreach (var worker in _celestialWorkers.Values) {
				await worker.StopWorker();
			}
			await StartWorker(ct, period, dueTime);
		}
		public bool IsWorkerRunning() {
			return (_timer != null) && (_timer.IsRunning);
		}
		public void SetSemaphore(SemaphoreSlim sem) {
			_sem.Dispose();
			_sem = sem;
		}

		public SemaphoreSlim GetSemaphore() {
			return _sem;
		}
		public async Task WaitWorker() {
			try {
				await _sem.WaitAsync(_ct);
			} catch (OperationCanceledException) {
			}
		}
		public void ReleaseWorker() {
			if (_sem.CurrentCount == 1) {
				DoLog(LogLevel.Warning, $"{GetWorkerName()} already released...");
			} else {
				_sem.Release();
			}
		}

		public abstract bool IsWorkerEnabledBySettings();
		public abstract string GetWorkerName();
		public abstract Feature GetFeature();
		public abstract LogSender GetLogSender();

		public DateTime? LastExecutionStart { get; private set; }
		public DateTime? LastExecutionEnd { get; private set; }

		// Watchdog needs to keep ticking even while the bot is "sleeping" - every other worker
		// intentionally pauses then, but a hang can happen at any time and sleep periods can last hours.
		protected virtual bool RunsDuringSleep => false;

		// Workers that check very frequently (e.g. Watchdog, every 1-2 min) can set this to false
		// so the "Next X execution in..." line doesn't dominate the log - most workers check every
		// 10-60+ min and the line is genuinely useful there. Not a log-level knob because every
		// sink here is configured at Verbose, so a lower level wouldn't actually get filtered out.
		protected virtual bool LogNextExecution => true;

	// Hook for workers that need to keep running in the background even when their
	// Active setting is false - the key use case is persisting expedition/discovery/farm
	// results to the database so the WebUI dashboards stay populated with lifetime totals
	// regardless of whether the worker itself is enabled. Called from ExecutionWrapper on
	// every tick while disabled, before the EndExecution/return path.
	protected virtual Task OnDisabledTick() => Task.CompletedTask;



		protected Task EndExecution() {
			// This is meant to be called within the worker callback, so we can't await _timer to end
			ChangeWorkerPeriod(Timeout.InfiniteTimeSpan);
			foreach (var worker in _celestialWorkers.Values) {
				worker.ChangeWorkerPeriod(Timeout.InfiniteTimeSpan);
			}
			return Task.CompletedTask;
		}

		private async Task ExecutionWrapper(CancellationToken ct) {

			if (_tbotInstance.UserData.isSleeping == true && !RunsDuringSleep) {
				DoLog(LogLevel.Debug, $"Sleeping... Ending {GetWorkerName()}");
				await EndExecution();
				return;
			} else if (IsWorkerEnabledBySettings() == false) {
				// Only announce "not enabled" once per disabled streak, not every restart cycle - a
				// disabled worker still gets Start/Restart cycled periodically (eg. by settings file
				// watches), and logging level can't filter this (every sink here is Verbose, see
				// LogNextExecution's comment above), so repeating it every cycle was pure log spam for
				// a feature the user has no intention of running.
				if (!_disabledStreakLogged) {
					DoLog(LogLevel.Information, $"{GetWorkerName()} not enabled by settings. Ending...");
					_disabledStreakLogged = true;
				}
				// Even when disabled, some workers need to keep persisting results (e.g. expedition
				// and discovery messages) so the WebUI dashboards retain lifetime totals.
				await OnDisabledTick();
				await EndExecution();
				return;
			}
			_disabledStreakLogged = false;

			try {
				await WaitWorker();

				ct.ThrowIfCancellationRequested();

				LastExecutionStart = DateTime.UtcNow;
				await Execute();
				LastExecutionEnd = DateTime.UtcNow;

				if (Period != Timeout.InfiniteTimeSpan) {
					if (LogNextExecution) {
						DoLog(LogLevel.Information, $"Next {GetWorkerName()} execution in {Period}");
					}
				}
				else {
					DoLog(LogLevel.Information, $"{GetWorkerName()} Stopped.");
				}
				
			} catch(OperationCanceledException) {
				// OK
			} finally {
				ReleaseWorker();
			}
		}

		private void RemoveAllTimers() {
			foreach (var tim in timers) {
				DoLog(LogLevel.Information, $"Deleting timer \"{tim.Key}\" for worker \"{GetWorkerName()}\"");
				tim.Value.Dispose();
			}
			timers.Clear();
		}
	}
}
