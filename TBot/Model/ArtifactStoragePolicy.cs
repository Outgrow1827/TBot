using System;
using System.IO;
using Newtonsoft.Json;

namespace TBot.Model {
	public enum ArtifactStorageState {
		Active,
		Paused
	}

	/// <summary>Decides whether AutoDiscovery pauses on artifact storage; two thresholds (stopAt/resumeBelow) keep the state from flapping.</summary>
	public static class ArtifactStoragePolicy {
		public static ArtifactStorageState NextState(
			ArtifactStorageState current,
			long collected,
			long stopAt,
			long resumeBelow) {
			if (stopAt <= 0)
				return ArtifactStorageState.Active;

			var resumeThreshold = Math.Min(Math.Max(0, resumeBelow), stopAt - 1);
			if (current == ArtifactStorageState.Paused)
				return collected <= resumeThreshold
					? ArtifactStorageState.Active
					: ArtifactStorageState.Paused;

			return collected >= stopAt
				? ArtifactStorageState.Paused
				: ArtifactStorageState.Active;
		}
	}

	public sealed class ArtifactStorageStateStore {
		private sealed class StateDocument {
			public ArtifactStorageState State { get; set; }
		}

		private readonly string _path;

		public ArtifactStorageStateStore(string alias) {
			var dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			Directory.CreateDirectory(dataFolder);
			_path = Path.Combine(dataFolder, $"autodiscovery_artifacts_{alias}.json");
		}

		public ArtifactStorageState Load() {
			if (!File.Exists(_path))
				return ArtifactStorageState.Active;

			try {
				return JsonConvert.DeserializeObject<StateDocument>(File.ReadAllText(_path))?.State
					?? ArtifactStorageState.Active;
			} catch {
				return ArtifactStorageState.Active;
			}
		}

		public void Save(ArtifactStorageState state) {
			try {
				var temporaryPath = _path + ".tmp";
				File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(new StateDocument { State = state }));
				if (File.Exists(_path))
					File.Replace(temporaryPath, _path, null);
				else
					File.Move(temporaryPath, _path);
			} catch {
				// A missing state file is safe: the next successful inventory read
				// starts from the active state and applies the threshold normally.
			}
		}
	}
}
