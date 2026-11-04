using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.DevSeed;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Seeder = Assembly.DevSeed.DevSeed;

namespace Assembly.Tests.Infrastructure.Development;

public sealed class LaneSeedTests
{
    [Fact]
    public void Local_and_remote_import_the_same_artificial_backup_without_colliding_or_resuming_providers()
    {
        var scratch = Directory.CreateTempSubdirectory("lane-seed-").FullName;
        try
        {
            var userHome = Path.Combine(scratch, "user");
            var local = Worktree(Path.Combine(scratch, "local"));
            var remote = Worktree(Path.Combine(userHome, "assembly-dotnet-example", "checkouts", "build"));
            File.WriteAllText(Path.Combine(userHome, "assembly-dotnet-example", "lane-number"), "2\n");
            var backup = Path.Combine(scratch, "safe-backup.db");
            using (var db = TestDatabase.Open(backup))
            {
                var rows = RowStore.Open(db);
                var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts");
                var session = sessions.Create("claude", "fixture-only");
                var batch = new RowBatch();
                batch.Rows.Add(new("source-run", null, "user", JsonSerializer.SerializeToElement(new { text = "Artificial archived conversation" }), 100));
                batch.Rows.Add(new("source-run", null, "turn", JsonSerializer.SerializeToElement(new { state = "finished" }), 101));
                rows.Commit(session.SessionId, batch);
            }
            var manifest = Path.Combine(scratch, "manifest.json");
            File.WriteAllText(manifest, SeedJson.Stringify(new SeedManifest(1, "test-backup", "synthetic",
                [new SeedSource { Format = "database", Path = backup, Sha256 = SeedFiles.HashFile(backup), Provider = "claude", ProviderConfidence = "verified", Provenance = ["Artificial SQLite backup created by LaneSeedTests"] }])));
            var localHome = SeedFiles.Profile(local, userHome);
            var remoteHome = SeedFiles.RemoteProfile(remote, new("example", 2), userHome);
            var localRequest = new SeedRequest(1, local, localHome, manifest);
            var remoteRequest = new SeedRequest(1, remote, remoteHome, manifest, RemoteLane: new("example", 2));
            var first = Seeder.RunOnHost(localRequest, userHome);
            var other = Seeder.RunOnHost(remoteRequest, userHome);
            Assert.Equal(first.UniqueSessions, other.UniqueSessions);
            Assert.Equal(1, first.InsertedSessions);
            Assert.Equal(1, other.InsertedSessions);
            Assert.Equal(Read(localHome, "SELECT kind||':'||payload FROM rows ORDER BY seq"), Read(remoteHome, "SELECT kind||':'||payload FROM rows ORDER BY seq"));
            Assert.Equal(Read(localHome, "SELECT title FROM session_info"), Read(remoteHome, "SELECT title FROM session_info"));
            Assert.NotEqual(Read(localHome, "SELECT session_id FROM session_info")[0], Read(remoteHome, "SELECT session_id FROM session_info")[0]);
            Assert.NotEqual(Read(localHome, "SELECT run_id FROM rows LIMIT 1")[0], Read(remoteHome, "SELECT run_id FROM rows LIMIT 1")[0]);
            Assert.Equal(["0"], Read(remoteHome, "SELECT COUNT(*) FROM session_info WHERE machine IS NOT NULL OR acp_session_id IS NOT NULL"));
            Assert.Equal(0, Seeder.RunOnHost(localRequest, userHome).InsertedSessions);
            Assert.Equal(0, Seeder.RunOnHost(remoteRequest, userHome).InsertedSessions);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [Fact]
    public void A_manifest_names_its_sources_relative_to_itself()
    {
        var scratch = Directory.CreateTempSubdirectory("lane-seed-relative-").FullName;
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp", "text.jsonl");
            Assert.True(File.Exists(fixture));
            Directory.CreateDirectory(Path.Combine(scratch, "scripts"));
            Directory.CreateDirectory(Path.Combine(scratch, "recordings"));
            File.Copy(fixture, Path.Combine(scratch, "recordings", "text.jsonl"));
            var manifest = Path.Combine(scratch, "scripts", "manifest.json");
            File.WriteAllText(manifest, SeedJson.Stringify(new SeedManifest(1, "relative", "real-recorded",
                [new SeedSource { Format = "acp", Path = "../recordings/text.jsonl", SessionKey = "text", Provenance = ["fixture"] }])));
            var bundle = SeedSources.Read(manifest);
            Assert.Single(bundle.Sessions);
            Assert.Contains(bundle.Inputs, input => input.Path == SeedFiles.Input(Path.Combine(scratch, "recordings", "text.jsonl")));
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [Theory]
    [InlineData("main", 1)]
    [InlineData("base", 1)]
    [InlineData("rail", 6)]
    [InlineData("remote", 1)]
    [InlineData("example", 0)]
    public void Remote_destination_refuses_protected_or_unreserved_lane_identity(string name, int number)
    {
        Assert.Throws<InvalidDataException>(() => SeedFiles.RemoteProfile("/not-a-checkout", new(name, number), "/unused"));
    }

    private static string Worktree(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "desktop"));
        Directory.CreateDirectory(Path.Combine(path, "dotnet", "Assembly.Desktop"));
        File.WriteAllText(Path.Combine(path, "desktop", "Cargo.toml"), "");
        File.WriteAllText(Path.Combine(path, "dotnet", "Assembly.Desktop", "Assembly.Desktop.csproj"), "<Project/>");
        return path;
    }
    private static string[] Read(string home, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(home, "assembly.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader(); var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }
}
