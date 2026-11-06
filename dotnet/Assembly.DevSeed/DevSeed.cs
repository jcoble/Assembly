using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.DevSeed;

/// <summary>Fills a checkout's dev profile with synthetic work. Only the profile is touched; nothing is read from elsewhere.</summary>
public static class DevSeed
{
    internal const string Marker = "dev-seed.json";
    // What a reset keeps: installed tools and the app's token, which take time to get back and hold no work.
    private static readonly HashSet<string> Kept = new(StringComparer.Ordinal)
        { "adapters", "agents.json", "env", "launcher.lock", "backend.lock", "backend.lock.pid", "backend.log", "remote-sync-state.json" };

    public static SeedResult Run(SeedRequest request, string userHome, CancellationToken cancel = default)
    {
        if (request.Mode is not (SeedMode.Reset or SeedMode.IfEmpty)) throw new InvalidDataException("Unknown seed mode.");
        var home = SeedProfile.Check(request.ComputedHome, request.CanonicalWorktree, userHome);
        SeedProfile.PrivateDirectory(home);
        using var owned = HomeLock.Take(home);
        var marker = Path.Combine(home, Marker);
        if (request.Mode == SeedMode.IfEmpty)
        {
            if (File.Exists(marker)) return new("kept");
            if (HasWork(Path.Combine(home, "assembly.db")))
            {
                // Remembered so later launches decide without building the seeder.
                SeedProfile.AtomicText(marker, SeedJson.Stringify(new { seeded = false }));
                return new("kept");
            }
        }
        Clear(home);
        cancel.ThrowIfCancellationRequested();
        var world = new SeedWorld(home, request.NowMs);
        SeedRepos.Build(world, SeedScenario.Projects, cancel);
        var sessions = SeedScenario.Sessions();
        SeedAttachments.Write(world, sessions);
        var counts = SeedDatabase.Write(world, sessions, cancel);
        SeedProfile.AtomicText(marker, SeedJson.Stringify(new { seeded = true }));
        return new("seeded", counts.Sessions, SeedScenario.Projects.Count, counts.Runs);
    }

    private static bool HasWork(string database)
    {
        if (!File.Exists(database)) return false;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('session_info','projects','saved_machines')";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (var table in tables)
        {
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table})";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) return true;
        }
        return false;
    }

    private static void Clear(string home)
    {
        foreach (var entry in new DirectoryInfo(home).EnumerateFileSystemInfos())
        {
            if (Kept.Contains(entry.Name)) continue;
            // A link is removed as a link: neither of these follows one.
            if (entry is DirectoryInfo { LinkTarget: null } directory) directory.Delete(recursive: true);
            else entry.Delete();
        }
    }
}
