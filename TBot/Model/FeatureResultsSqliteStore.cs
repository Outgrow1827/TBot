using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TBot.Model {
	// Permanent (never trimmed) counterpart to FeatureResultsStore's capped 200-entry JSON file -
	// requested live 2026-09-02: the user wants expedition/discovery results kept forever, not just
	// the most recent 200 (which get silently evicted as new ones arrive). Same SQLite-in-data/
	// pattern as AutoFarmStateStore's "attacks" table (already unbounded, used as the reference).
	// Deliberately minimal: one INSERT OR IGNORE (Id-based dedup, same reasoning as
	// FeatureResultsStore's in-memory dedup) plus one aggregate SUM query for the dashboard's
	// lifetime totals - the JSON store remains the source for the "recent results" table itself.
	public sealed class FeatureResultsSqliteStore : IDisposable {
		private readonly string _connectionString;

		static FeatureResultsSqliteStore() {
			SQLitePCL.Batteries_V2.Init();
		}

		public FeatureResultsSqliteStore(string fileName, string dataFolder = null) {
			var folder = dataFolder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			Directory.CreateDirectory(folder);
			var safeFileName = Path.GetFileName(fileName);
			var dbPath = Path.Combine(folder, safeFileName);

			_connectionString = new SqliteConnectionStringBuilder {
				DataSource = dbPath,
				Pooling = false
			}.ToString();

			using var connection = new SqliteConnection(_connectionString);
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = @"
CREATE TABLE IF NOT EXISTS results (
	id INTEGER PRIMARY KEY,
	coordinate TEXT,
	summary TEXT,
	metal INTEGER NOT NULL DEFAULT 0,
	crystal INTEGER NOT NULL DEFAULT 0,
	deuterium INTEGER NOT NULL DEFAULT 0,
	darkmatter INTEGER NOT NULL DEFAULT 0,
	artifacts_found INTEGER NOT NULL DEFAULT 0,
	artifacts_or_lifeform TEXT,
	timestamp_utc TEXT NOT NULL
);";
			command.ExecuteNonQuery();
		}

		public void Add(FeatureResultEntry entry) {
			if (entry == null)
				return;

			try {
				using var connection = new SqliteConnection(_connectionString);
				connection.Open();
				using var command = connection.CreateCommand();
				command.CommandText = @"
INSERT OR IGNORE INTO results
	(id, coordinate, summary, metal, crystal, deuterium, darkmatter, artifacts_found, artifacts_or_lifeform, timestamp_utc)
VALUES
	($id, $coordinate, $summary, $metal, $crystal, $deuterium, $darkmatter, $artifactsFound, $artifactsOrLifeform, $timestampUtc);";
				command.Parameters.AddWithValue("$id", entry.Id);
				command.Parameters.AddWithValue("$coordinate", entry.Coordinate ?? "");
				command.Parameters.AddWithValue("$summary", entry.Summary ?? "");
				command.Parameters.AddWithValue("$metal", entry.Metal);
				command.Parameters.AddWithValue("$crystal", entry.Crystal);
				command.Parameters.AddWithValue("$deuterium", entry.Deuterium);
				command.Parameters.AddWithValue("$darkmatter", entry.Darkmatter);
				command.Parameters.AddWithValue("$artifactsFound", entry.ArtifactsFound);
				command.Parameters.AddWithValue("$artifactsOrLifeform", entry.ArtifactsOrLifeform ?? "");
				command.Parameters.AddWithValue("$timestampUtc", entry.TimestampUtc.ToString("O"));
				command.ExecuteNonQuery();
			} catch {
				// Permanent persistence must never stop the worker - the capped JSON store still
				// covers the "recent results" view even if this write fails.
			}
		}

		public (long Metal, long Crystal, long Deuterium, long Darkmatter, long ArtifactsFound) GetLifetimeTotal() {
			try {
				using var connection = new SqliteConnection(_connectionString);
				connection.Open();
				using var command = connection.CreateCommand();
				command.CommandText = @"
SELECT COALESCE(SUM(metal),0), COALESCE(SUM(crystal),0), COALESCE(SUM(deuterium),0),
	COALESCE(SUM(darkmatter),0), COALESCE(SUM(artifacts_found),0)
FROM results;";
				using var reader = command.ExecuteReader();
				if (reader.Read())
					return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
			} catch {
				// Fall through to zeroed total - the dashboard already tolerates this being empty.
			}
			return (0, 0, 0, 0, 0);
		}

		public void Dispose() { }
	}
}
