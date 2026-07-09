using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Tbot.Services {
	public class SettingsFileWatcher {

		private Func<Task> _watchFunc;
		private string _absFpToWatch;
		private SemaphoreSlim _changedSem = new SemaphoreSlim(1, 1);
		// Some filesystems (VMware shared folders in particular) can fire many change notifications for a
		// single logical edit in a very short burst. Without this, each one used to kick off its own fully
		// concurrent _watchFunc() run (see history: "async void" callback meant the semaphore only guarded
		// the synchronous part of the call, not the actual reload), and dozens/hundreds of overlapping
		// reloads racing on shared static state (InstanceManager.instances, worker lists, ...) is exactly
		// what caused "Collection was modified" / "task already completed" crashes in practice. Now a burst
		// collapses to: one run processes the file as it is when it starts, and if any further changes
		// arrived while it was busy, exactly one more run happens right after (to pick up the latest state) -
		// never a pile of overlapping runs.
		private volatile bool _runPending;
		private PhysicalFileProvider p;
		private IChangeToken changeToken;
		private IDisposable changeCallback;

		public SettingsFileWatcher(Func<Task> func, string filePathToWatch) {
			_watchFunc = func;
			_absFpToWatch = filePathToWatch;

			initWatch();
		}

		private void initWatch() {
			p = new PhysicalFileProvider(Path.GetDirectoryName(_absFpToWatch));
			changeToken = p.Watch(Path.GetFileName(_absFpToWatch));
			changeCallback = changeToken.RegisterChangeCallback(onChanged, default);
		}

		public void deinitWatch() {
			if (changeCallback != null) {
				changeCallback.Dispose();
				changeCallback = null;
			}
		}

		private async void onChanged(object state) {
			// Re-arm the watch immediately so we don't miss a change that happens while we're busy below.
			initWatch();

			if (!await _changedSem.WaitAsync(0)) {
				// A run is already in flight: just flag that another pass is needed once it's done,
				// instead of queuing up an unbounded number of concurrent runs.
				_runPending = true;
				return;
			}

			try {
				do {
					_runPending = false;
					await _watchFunc();
				} while (_runPending);
			} finally {
				_changedSem.Release();
			}
		}
	}
}
