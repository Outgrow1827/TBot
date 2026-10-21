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

		private Action _watchFunc;
		private string _absFpToWatch;
		private SemaphoreSlim _changedSem = new SemaphoreSlim(1, 1);
		private PhysicalFileProvider p;
		private IChangeToken changeToken;
		private IDisposable changeCallback;

		public SettingsFileWatcher(Action func, string filePathToWatch) {
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
			// async void callback: the finally must release _changedSem and re-init the watch, or one failed reload would stop the watcher for good.
			await _changedSem.WaitAsync();
			try {
				_watchFunc();
			} catch {
				// Swallowed on purpose: the watch callback logs its own failures; this only has to keep
				// the watcher alive.
			} finally {
				_changedSem.Release();
				initWatch();
			}
		}
	}
}
