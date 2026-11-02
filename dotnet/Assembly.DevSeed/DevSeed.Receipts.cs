namespace Assembly.DevSeed;

public static partial class DevSeed
{
    private sealed record ImportState(bool Existing, bool Pending, bool Complete, SeedReceipt Receipt,
        IReadOnlyList<Replica> NewReplicas, IReadOnlyList<SeedFolder> NewFolders);

    private static SeedReceipt ReadReceipt(string path) => SeedJson.Parse<SeedReceipt>(File.ReadAllText(SeedFiles.Input(path)));
    private static bool SameFiles(SeedFolder a, SeedFolder b) => a.Files.Count == b.Files.Count &&
        a.Files.All(pair => b.Files.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) => a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    // Completed receipts identify candidates only. Actual SQLite rows and every immutable file are checked below.
    private static ImportState Inspect(string databasePath, string directory, string final, string pending,
        SeedReceipt template, IReadOnlyList<Replica> replicas, IReadOnlyList<SeedFolder> folders)
    {
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        var knownFolders = new HashSet<string>(StringComparer.Ordinal);
        SeedFiles.SafePath(directory, false);
        if (Directory.Exists(directory))
        {
            foreach (var other in Directory.EnumerateFiles(directory, "*.pending.json"))
                if (other != pending) throw new InvalidDataException("Another pending seed receipt requires retrying its exact manifest first.");
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Where(p => !p.EndsWith(".pending.json", StringComparison.Ordinal)))
            {
                var completed = ReadReceipt(path);
                if (completed.Version != 1) throw new InvalidDataException("Unsupported private seed receipt version.");
                if (completed.OriginId != template.OriginId) continue;
                foreach (var replica in replicas)
                    foreach (var prior in completed.Replicas.Where(p => p.Id == replica.Id))
                    {
                        if (prior.OriginalKey != replica.OriginalKey || prior.Fingerprint != replica.Fingerprint)
                            throw new InvalidDataException("Prior source identity has different expected fingerprints.");
                        knownIds.Add(replica.Id);
                    }
                foreach (var folder in folders)
                    foreach (var prior in completed.Folders.Where(p => p.Path == folder.Path))
                    {
                        if (!SameFiles(prior, folder)) throw new InvalidDataException("Existing immutable fixture folder would change.");
                        knownFolders.Add(folder.Path);
                    }
            }
        }
        var hasPending = File.Exists(pending);
        var priorReceipt = File.Exists(final) ? ReadReceipt(final) : hasPending ? ReadReceipt(pending) : null;
        if (priorReceipt is not null && SeedJson.Stringify(priorReceipt with { OwnedSessionIds = null, OwnedFolderPaths = null }) != SeedJson.Stringify(template))
            throw new InvalidDataException("Private receipt identity conflict.");
        var newReplicas = replicas.Where(r => !knownIds.Contains(r.Id)).ToArray();
        var newFolders = folders.Where(f => !knownFolders.Contains(f.Path)).ToArray();
        if (hasPending && !File.Exists(final) && priorReceipt is not null &&
            (!SameSet(priorReceipt.OwnedSessionIds ?? replicas.Select(r => r.Id).ToArray(), newReplicas.Select(r => r.Id)) ||
             !SameSet(priorReceipt.OwnedFolderPaths ?? folders.Select(f => f.Path).ToArray(), newFolders.Select(f => f.Path))))
            throw new InvalidDataException("Pending import ownership disagrees with completed prior imports.");
        var allowed = knownIds.Concat(hasPending ? newReplicas.Select(r => r.Id) : []).ToHashSet(StringComparer.Ordinal);
        var existing = File.Exists(databasePath);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (existing)
        {
            using var read = Target(databasePath);
            found = CheckReplicas(read, replicas, allowed);
        }
        if (!knownIds.IsSubsetOf(found)) throw new InvalidDataException("A previously imported replica is missing; refusing to replace user changes.");
        var newlyFound = newReplicas.Count(r => found.Contains(r.Id));
        if (newlyFound != 0 && newlyFound != newReplicas.Length) throw new InvalidDataException("Mixed/partial destination import is refused.");
        foreach (var folder in folders)
        {
            SeedFiles.SafePath(folder.Path, false);
            if (knownFolders.Contains(folder.Path)) ValidateFolder(folder);
            else if (Directory.Exists(folder.Path))
            {
                if (!hasPending) throw new InvalidDataException("Existing asset folder collision.");
                ValidateFolder(folder);
            }
            else if (File.Exists(folder.Path)) throw new InvalidDataException("Existing asset file collision.");
        }
        var receipt = template with { OwnedSessionIds = newReplicas.Select(r => r.Id).ToArray(), OwnedFolderPaths = newFolders.Select(f => f.Path).ToArray() };
        return new(existing, hasPending, existing && found.Count == replicas.Count, receipt, newReplicas, newFolders);
    }

    // A committed new database is published without repeating writes. An initialized empty stage can be retried.
    private static bool RecoverStagedDatabase(string path, IReadOnlyList<Replica> replicas)
    {
        if (!File.Exists(path)) return false;
        using var stage = Target(path);
        var found = CheckReplicas(stage, replicas, replicas.Select(r => r.Id).ToHashSet(StringComparer.Ordinal));
        using var count = stage.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM session_info;";
        if (found.Count != 0 && found.Count != replicas.Count || Convert.ToInt64(count.ExecuteScalar()) != found.Count)
            throw new InvalidDataException("Unrecognized private staged database state.");
        var expectedRows = replicas.Where(r => found.Contains(r.Id)).Sum(r => r.Rows.Count);
        var expectedDetails = replicas.Where(r => found.Contains(r.Id)).Sum(r => r.Details.Count);
        foreach (var (table, expected) in new[] {("sessions",(long)found.Count),("rows",(long)expectedRows),("tool_details",(long)expectedDetails),
            ("session_config",0L),("session_commands",0L),("helper_messages",0L),("agent_config",0L),("agent_commands",0L)})
        {
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            if (Convert.ToInt64(count.ExecuteScalar()) != expected) throw new InvalidDataException("User-modified private staged database.");
        }
        count.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT IN ('sessions','rows','tool_details','session_info','session_config','session_commands','helper_messages','agent_config','agent_commands','audit');";
        if (Convert.ToInt64(count.ExecuteScalar()) != 0) throw new InvalidDataException("Unknown private staged database tables.");
        return found.Count == replicas.Count;
    }

    private static void RecoverOwnedFolders(IReadOnlyList<SeedFolder> folders, string digest)
    {
        // Validate all ownership before deleting any path. Prior imports' folders are never included here.
        foreach (var folder in folders)
        {
            var staging = folder.Path + ".seed-" + digest + ".staging";
            if (Directory.Exists(folder.Path)) ValidateFolder(folder);
            if (Directory.Exists(staging)) ValidateFolder(folder with { Path = staging }, true);
        }
        foreach (var folder in folders)
        {
            var staging = folder.Path + ".seed-" + digest + ".staging";
            if (Directory.Exists(folder.Path)) Directory.Delete(folder.Path, true);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
