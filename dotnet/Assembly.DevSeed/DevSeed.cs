using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Topics;
using Microsoft.Data.Sqlite;

namespace Assembly.DevSeed;

public sealed record Replica(string OriginalKey, string Id, string TopId, string? ParentId, string Agent, string Title,
    SeedMetadata Metadata, IReadOnlyList<SeedRow> Rows, IReadOnlyList<ToolDetail> Details, string Fingerprint,
    IReadOnlyDictionary<string, string> RunMap, IReadOnlyDictionary<string, string> ItemMap);
public sealed record SeedFolder(string Path, IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Sources);
public sealed record ReceiptReplica(string OriginalKey, string Id, string TopId, string? ParentId, SeedMetadata OriginalMetadata,
    string ProviderClaim, string ProviderConfidence, string? OriginalHost, IReadOnlyList<string> Provenance, IReadOnlyList<SeedInput> SourceInputs, string Fingerprint, IReadOnlyList<string> RowFingerprints, IReadOnlyList<string> DetailFingerprints,
    IReadOnlyDictionary<string, string> RunMap, IReadOnlyDictionary<string, string> ItemMap);
public sealed record SeedReceipt(int Version, string Digest, string OriginId, IReadOnlyList<SeedInput> Inputs,
    IReadOnlyList<ReceiptReplica> Replicas, IReadOnlyList<SeedFolder> Folders, IReadOnlyList<string> Transformations, IReadOnlyList<string> Limitations, IReadOnlyList<string>? OwnedSessionIds = null, IReadOnlyList<string>? OwnedFolderPaths = null);

public static partial class DevSeed
{
    // Tests inject crashes at transaction/asset boundaries; production has no injection input.
    public static SeedReport Run(SeedRequest request, CancellationToken cancellationToken = default, Action<string>? fault = null)
    {
        if (request.Version != 1) throw new InvalidDataException("Unsupported request version.");
        var worktree = SeedFiles.SafePath(request.CanonicalWorktree);
        var home = SeedFiles.SafePath(SeedFiles.Profile(worktree), false);
        if (request.ComputedHome != home) throw new InvalidDataException("Destination must be the exact launcher-derived profile.");
        var bundle = SeedSources.Read(request.ManifestPath, cancellationToken);
        if (SeedFiles.OverlapsHome(bundle.Inputs.Select(input => input.Path), home)) throw new InvalidDataException("Source/target overlap is refused.");
        var replicas = Transform(bundle, home);
        var folders = Folders(bundle, replicas, home);
        var digest = SeedFiles.Hash(SeedJson.Stringify(new { home, bundle.Manifest.OriginId, bundle.Inputs, replicas, folders }));
        var originals = bundle.Sessions.ToDictionary(s => s.Key);
        var template = new SeedReceipt(1, digest, bundle.Manifest.OriginId, bundle.Inputs, replicas.Select(r => new ReceiptReplica(r.OriginalKey, r.Id, r.TopId, r.ParentId, originals[r.OriginalKey].Metadata, originals[r.OriginalKey].Provider,
                originals[r.OriginalKey].ProviderConfidence, originals[r.OriginalKey].OriginalHost,
                originals[r.OriginalKey].Provenance, originals[r.OriginalKey].Inputs ?? [], r.Fingerprint,
                r.Rows.Select(row => SeedFiles.Hash(SeedJson.Stringify(row))).ToArray(), r.Details.Select(d => SeedFiles.Hash(SeedJson.Stringify(d))).ToArray(), r.RunMap, r.ItemMap)).ToArray(), folders,
            ["Fresh deterministic session/run identities", "Provider resume handles and machines removed", "Generated isolated fixture repository; surrounding files are synthetic unless explicitly listed", "Only recognized exact attachment/file references rewritten"], bundle.Limitations);
        var directory = Path.Combine(home, "seed-imports");
        var final = Path.Combine(directory, digest + ".json"); var pending = Path.Combine(directory, digest + ".pending.json");
        var databasePath = Path.Combine(home, "assembly.db"); var stageDatabase = databasePath + ".seed-" + digest + ".staging";
        if (request.DryRun)
        {
            Inspect(databasePath, directory, final, pending, template, replicas, folders);
            return Report(bundle, 0, 0, 0, 0, null, true);
        }
        SeedFiles.PrivateDirectory(home);
        foreach (var lockName in new[] { "backend.lock", "backend.lock.pid" })
        {
            var lockPath = Path.Combine(home, lockName);
            SeedFiles.SafePath(lockPath, false);
            if (File.Exists(lockPath)) SeedFiles.Input(lockPath);
        }
        using var ownedHome = HomeLock.Take(home);
        cancellationToken.ThrowIfCancellationRequested();
        SeedFiles.PrivateDirectory(directory);
        var state = Inspect(databasePath, directory, final, pending, template, replicas, folders);
        var receipt = state.Receipt;
        if (state.Complete)
        {
            ValidateFolders(folders);
            if (!File.Exists(final)) SeedFiles.AtomicJson(final, receipt);
            if (File.Exists(pending)) File.Delete(pending);
            return Report(bundle, 0, 0, 0, 0, final, false);
        }
        if (state.Pending)
        {
            var stagedCommitted = !state.Existing && RecoverStagedDatabase(stageDatabase, state.NewReplicas);
            if (stagedCommitted)
            {
                ValidateFolders(folders);
                using (var file = new FileStream(stageDatabase, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
                File.Move(stageDatabase, databasePath);
                SeedFiles.SyncDirectory(home);
                SeedFiles.AtomicJson(final, receipt);
                File.Delete(pending);
                return Report(bundle, 0, 0, 0, 0, final, false);
            }
            RecoverOwnedFolders(state.NewFolders, digest);
            if (File.Exists(stageDatabase)) File.Delete(stageDatabase);
        }
        CheckFolderPaths(state.NewFolders, false);
        SeedFiles.AtomicJson(pending, receipt);
        foreach (var input in bundle.Inputs)
            if (SeedFiles.HashFile(SeedFiles.Input(input.Path)) != input.Sha256) throw new InvalidDataException("Source changed during import preparation.");
        foreach (var folder in state.NewFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stage(folder, digest, fault);
            fault?.Invoke("asset-prepared");
        }
        var existing = state.Existing;
        var actualPath = existing ? databasePath : stageDatabase;
        if (!existing && File.Exists(stageDatabase)) throw new InvalidDataException("Private staging database collision.");
        if (!existing)
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var privateFile = new FileStream(stageDatabase, options);
            privateFile.Flush(true);
        }
        using (var database = AppDatabase.Open(actualPath, TimeProvider.System, new Topics()))
        {
            if (!existing)
            {
                RowStore.Open(database);
                SessionStore.Open(database, TimeProvider.System, Path.Combine(home, "sessions"));
            }
            database.Writer.Write("dev-seed.import", scope =>
            {
                foreach (var replica in state.NewReplicas)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Insert(scope, replica, home, fault);
                }
                if (scope.QueryFirst("PRAGMA integrity_check;", r => r.Text(0)) != "ok") throw new InvalidDataException("Import integrity check failed.");
                if (scope.Query("PRAGMA foreign_key_check;", r => r.Text(0)).Count != 0) throw new InvalidDataException("Import foreign-key check failed.");
                foreach (var replica in replicas)
                {
                    var id = new SqlArg("$id", replica.Id);
                    if (scope.QueryFirst("SELECT next_seq FROM sessions WHERE session_id=$id;", r => r.Long(0), id) != Next(replica.Rows) ||
                        scope.QueryFirst("SELECT COUNT(*) FROM rows WHERE session_id=$id;", r => r.Long(0), id) != replica.Rows.Count ||
                        scope.QueryFirst("SELECT COUNT(*) FROM tool_details WHERE session_id=$id;", r => r.Long(0), id) != replica.Details.Count ||
                        replica.ParentId is not null && scope.QueryFirst("SELECT COUNT(*) FROM session_info WHERE session_id=$id;", r => r.Long(0), new SqlArg("$id", replica.ParentId)) != 1)
                        throw new InvalidDataException("Imported relational invariant failed.");
                }
                fault?.Invoke("before-commit");
                return 0;
            });
        }
        fault?.Invoke("after-commit");
        if (!existing)
        {
            using (var file = new FileStream(stageDatabase, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            File.Move(stageDatabase, databasePath);
            SeedFiles.SyncDirectory(home);
        }
        fault?.Invoke("after-publish");
        SeedFiles.AtomicJson(final, receipt);
        File.Delete(pending);
        return Report(bundle, state.NewReplicas.Count, state.NewReplicas.Sum(s => s.Rows.Count), state.NewReplicas.Sum(s => s.Details.Count), state.NewFolders.Sum(f => f.Files.Count), final, false);
    }
    private static SeedReport Report(SeedBundle b, int sessions, int rows, int details, int files, string? receipt, bool dry) =>
        new(b.Inputs.Count, b.Sessions.Count, b.Sessions.Sum(s => s.Rows.Count), b.Sessions.Sum(s => s.Rows.Where(r => r.Kind == "tool").Select(r => r.ItemId).Distinct().Count()), b.Sessions.Sum(s => s.Details.Count),
            b.Sessions.Sum(s => s.Files.Count) + 3 * b.Sessions.Count(s => s.Metadata.ParentSessionKey is null), b.Sessions.Sum(s => s.Attachments.Count), sessions, rows, details, files, b.DeduplicatedRows, b.SkippedRemote,
            b.Manifest.Kind == "real-recorded" ? b.Sessions.Count : 0, b.Manifest.Kind == "synthetic" ? b.Sessions.Count : 0,
            b.Sessions.Select(s => s.Provider + ":" + s.ProviderConfidence).Distinct().ToArray(), b.Sessions.Select(s => s.OriginalHost ?? "unknown").Distinct().ToArray(), b.Limitations, receipt, dry, 3 * b.Sessions.Count(s => s.Metadata.ParentSessionKey is null));
    private static long Next(IReadOnlyList<SeedRow> rows) => rows.Count == 0 ? 1 : rows.Max(r => r.Seq) + 1;
    public static string SessionId(string origin, string key) => "seed-" + SeedFiles.Hash(SeedJson.Stringify(new[] { origin, key }));
    public static string RunId(string origin, string key, string run) => "seed-run-" + SeedFiles.Hash(SeedJson.Stringify(new[] { origin, key, run }));
    private static List<Replica> Transform(SeedBundle bundle, string home)
    {
        var result = new List<Replica>(); var byKey = bundle.Sessions.ToDictionary(s => s.Key);
        foreach (var session in bundle.Sessions)
        {
            var id = SessionId(bundle.Manifest.OriginId, session.Key); var top = session;
            while (top.Metadata.ParentSessionKey is { } parent) top = byKey[parent];
            var topId = SessionId(bundle.Manifest.OriginId, top.Key);
            var runMap = session.Rows.Where(r => r.RunId is not null).Select(r => r.RunId!).Distinct().ToDictionary(r => r, r => RunId(bundle.Manifest.OriginId, session.Key, r));
            var itemMap = new Dictionary<string, string>();
            string? Item(SeedRow row)
            {
                if (row.ItemId is not { } item || row.RunId is not { } run) return row.ItemId;
                string? replacement = row.Kind switch
                {
                    "plan" when item == run + ":plan" => runMap[run] + ":plan",
                    "context" when item == "context:" + run => "context:" + runMap[run],
                    "message" or "delta" or "thinking" when item.StartsWith(run + ":", StringComparison.Ordinal) && Unnamed().IsMatch(item[(run.Length + 1)..]) => runMap[run] + item[run.Length..],
                    _ => null
                };
                if (replacement is not null) itemMap[item] = replacement;
                return replacement ?? item;
            }
            var mappings = bundle.Sessions.Where(s => TopKey(s, byKey) == top.Key).SelectMany(s => s.Files).Where(f => f.OriginalPath is not null)
                .GroupBy(f => f.OriginalPath!).ToDictionary(g => g.Key, g => g.Select(f => f.RelativePath).Distinct().Single());
            string? FilePath(string? path) => path is not null && mappings.TryGetValue(path, out var mapped) ? mapped : path;
            var rows = new List<SeedRow>();
            foreach (var row in session.Rows)
            {
                var payload = JsonNode.Parse(row.Payload.GetRawText())!.AsObject();
                if (row.Kind == "stats" && payload["runId"]?.GetValue<string>() is { } statsRun)
                {
                    if (!runMap.TryGetValue(statsRun, out var mapped)) throw new InvalidDataException("Stats references an unknown run.");
                    payload["runId"] = mapped;
                }
                if (row.Kind == "subagent")
                {
                    payload["sessionId"] = SessionId(bundle.Manifest.OriginId, payload["sessionId"]!.GetValue<string>());
                    payload["canStop"] = false;
                }
                if (row.Kind == "task") payload["canStop"] = false;
                if (row.Kind == "tool")
                {
                    if (payload["path"] is JsonValue p) payload["path"] = FilePath(p.GetValue<string>());
                    if (payload["files"] is JsonArray files) foreach (var file in files) file!["path"] = FilePath(file["path"]!.GetValue<string>());
                }
                if (row.Kind == "user" && payload["text"] is JsonValue text)
                {
                    var lines = text.GetValue<string>().Split('\n');
                    for (var i = 0; i < lines.Length; i++) foreach (var a in session.Attachments)
                        if (lines[i] == "Attached image: " + a.OriginalPath) lines[i] = "Attached image: " + Path.Combine(home, "attachments", id, a.RelativePath);
                    payload["text"] = string.Join('\n', lines);
                    payload.Remove("clientId"); payload.Remove("from");
                }
                rows.Add(row with { RunId = row.RunId is null ? null : runMap[row.RunId], ItemId = Item(row), Payload = JsonSerializer.SerializeToElement(payload, SeedJson.Options) });
            }
            var details = session.Details.Select(d => d with { Path = FilePath(d.Path) }).ToArray();
            var metadata = session.Metadata with { ReadSeq = Math.Clamp(session.Metadata.ReadSeq, 0, Next(rows) - 1) };
            var title = "Recorded" + (session.OriginalHost is null ? "" : " [" + session.OriginalHost + "]") + ": " + (metadata.Title ?? session.Key);
            var replica = new Replica(session.Key, id, topId, metadata.ParentSessionKey is null ? null : SessionId(bundle.Manifest.OriginId, metadata.ParentSessionKey), session.Provider, title, metadata, rows, details, "", runMap, itemMap);
            result.Add(replica with { Fingerprint = Fingerprint(replica) });
        }
        return result;
    }
    [GeneratedRegex("^[0-9]+(?::thinking)?$")]
    private static partial Regex Unnamed();
    private static string TopKey(SeedSession session, Dictionary<string, SeedSession> sessions)
    { while (session.Metadata.ParentSessionKey is { } key) session = sessions[key]; return session.Key; }
    private static string Fingerprint(Replica r) => SeedFiles.Hash(SeedJson.Stringify(new { r.Id, r.ParentId, r.TopId, r.Agent, r.Title, CreatedAt = r.Metadata.CreatedAt, UpdatedAt = r.Metadata.UpdatedAt, r.Metadata.SessionCost, r.Metadata.ReadSeq, r.Rows, r.Details }));
    private static SqliteConnection Target(string path)
    {
        SeedFiles.Input(path);
        // Stopped targets may retain a WAL; read it without migrating or writing any schema.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); try
        {
            SeedSources.ValidateSchema(db);
            using var schema = db.CreateCommand();
            foreach (var (table, columns) in new[] { ("session_config","session_id,options,choices"), ("agent_config","agent,options"),
                ("session_commands","session_id,commands"), ("agent_commands","agent,commands"),
                ("helper_messages","message_id,from_session,to_session,text,run_id,created_at,error,state,reply") })
            {
                schema.CommandText = $"PRAGMA table_info({table});";
                var actual = new HashSet<string>(); using (var reader = schema.ExecuteReader()) while (reader.Read()) actual.Add(reader.GetString(1));
                if (columns.Split(',').Any(column => !actual.Contains(column)))
                    throw new InvalidDataException("Missing current target schema; initialize isolated profile through its normal launcher.");
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static HashSet<string> CheckReplicas(SqliteConnection db, IReadOnlyList<Replica> replicas, HashSet<string> allowed)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in replicas)
        {
            using var c = db.CreateCommand(); c.Parameters.AddWithValue("$id", r.Id);
            c.CommandText = "SELECT agent,title,parent_session_id,created_at,updated_at,session_cost,read_seq,machine,acp_session_id,repo_url,helper,completed_at,settled_at,title_source FROM session_info WHERE session_id=$id;";
            Replica? actual = null;
            using (var reader = c.ExecuteReader()) if (reader.Read())
            {
                if (!allowed.Contains(r.Id) || !reader.IsDBNull(7) || !reader.IsDBNull(8) || reader.GetString(9) != Path.Combine(Path.GetDirectoryName(db.DataSource)!, "sessions", r.TopId) || reader.GetInt64(10) != 0 || !reader.IsDBNull(12) || !reader.IsDBNull(13) || reader.IsDBNull(11) || reader.GetInt64(11) != r.Metadata.UpdatedAt)
                    throw new InvalidDataException("Divergent replica identity/live state.");
                actual = r with { Agent = reader.GetString(0), Title = reader.GetString(1), ParentId = reader.IsDBNull(2) ? null : reader.GetString(2), Metadata = r.Metadata with { CreatedAt = reader.GetInt64(3), UpdatedAt = reader.GetInt64(4), SessionCost = reader.IsDBNull(5) ? null : reader.GetString(5), ReadSeq = reader.GetInt64(6) } };
            }
            if (actual is not null)
            {
                var rows = new List<SeedRow>(); c.CommandText = "SELECT seq,run_id,item_id,kind,payload,created_at FROM rows WHERE session_id=$id ORDER BY seq;";
                using (var reader = c.ExecuteReader()) while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(), reader.GetInt64(5)));
                var details = new List<ToolDetail>(); c.CommandText = "SELECT item_id,input,output,diff,path FROM tool_details WHERE session_id=$id ORDER BY item_id;";
                using (var reader = c.ExecuteReader()) while (reader.Read()) details.Add(new(reader.GetString(0), Nullable(reader, 1), Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4)));
                actual = actual with { Rows = rows, Details = details };
                c.CommandText = "SELECT next_seq FROM sessions WHERE session_id=$id;";
                if (Convert.ToInt64(c.ExecuteScalar()) != Next(r.Rows) || Fingerprint(actual) != r.Fingerprint) throw new InvalidDataException("Divergent seeded session; never overwrite user changes.");
                // Draft/config/command/helper state added to an archival replica also makes it divergent.
                c.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('session_config','session_commands','helper_messages');";
                if (Convert.ToInt64(c.ExecuteScalar()) != 3) throw new InvalidDataException("Missing current target schema.");
                c.CommandText = "SELECT (SELECT COUNT(*) FROM session_config WHERE session_id=$id)+(SELECT COUNT(*) FROM session_commands WHERE session_id=$id)+(SELECT COUNT(*) FROM helper_messages WHERE from_session=$id OR to_session=$id);";
                if (Convert.ToInt64(c.ExecuteScalar()) != 0) throw new InvalidDataException("Replica now has user configuration or helper state.");
                found.Add(r.Id);
            }
            else
            {
                c.CommandText = "SELECT (SELECT COUNT(*) FROM sessions WHERE session_id=$id)+(SELECT COUNT(*) FROM rows WHERE session_id=$id)+(SELECT COUNT(*) FROM tool_details WHERE session_id=$id);";
                if (Convert.ToInt64(c.ExecuteScalar()) != 0) throw new InvalidDataException("Orphaned replica destination rows.");
            }
            foreach (var run in r.RunMap.Values)
            {
                c.Parameters.Clear(); c.Parameters.AddWithValue("$run", run); c.Parameters.AddWithValue("$id", r.Id);
                c.CommandText = "SELECT COUNT(*) FROM rows WHERE run_id=$run AND session_id<>$id;";
                if (Convert.ToInt64(c.ExecuteScalar()) != 0) throw new InvalidDataException("Destination run-ID collision.");
            }
        }
        return found;
    }
    private static string? Nullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static void Insert(IWriteScope scope, Replica r, string home, Action<string>? fault)
    {
        scope.Execute("INSERT INTO session_info(session_id,agent,repo_url,title,acp_session_id,created_at,updated_at,machine,session_cost,parent_session_id,helper,read_seq,completed_at,settled_at,title_source) VALUES($id,$agent,$repo,$title,NULL,$created,$updated,NULL,$cost,$parent,0,$read,$updated,NULL,NULL);",
            new SqlArg("$id", r.Id), new("$agent", r.Agent), new("$repo", Path.Combine(home, "sessions", r.TopId)), new("$title", r.Title), new("$created", r.Metadata.CreatedAt), new("$updated", r.Metadata.UpdatedAt), new("$cost", r.Metadata.SessionCost), new("$parent", r.ParentId), new("$read", r.Metadata.ReadSeq));
        scope.Execute("INSERT INTO sessions(session_id,next_seq) VALUES($id,$next);", new SqlArg("$id", r.Id), new("$next", Next(r.Rows)));
        foreach (var row in r.Rows)
        {
            fault?.Invoke("before-row");
            scope.Execute("INSERT INTO rows(session_id,seq,run_id,item_id,kind,payload,created_at) VALUES($id,$seq,$run,$item,$kind,$payload,$at);",
                new SqlArg("$id", r.Id), new("$seq", row.Seq), new("$run", row.RunId), new("$item", row.ItemId), new("$kind", row.Kind), new("$payload", row.Payload.GetRawText()), new("$at", row.CreatedAt));
        }
        foreach (var d in r.Details) scope.Execute("INSERT INTO tool_details(session_id,item_id,input,output,diff,path) VALUES($id,$item,$input,$output,$diff,$path);", new SqlArg("$id", r.Id), new("$item", d.ItemId), new("$input", d.Input), new("$output", d.Output), new("$diff", d.Diff), new("$path", d.Path));
    }
    private static List<SeedFolder> Folders(SeedBundle b, IReadOnlyList<Replica> replicas, string home)
    {
        var result = new List<SeedFolder>();
        foreach (var top in replicas.Where(r => r.ParentId is null))
        {
            var contents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SEED-FIXTURE.txt"] = "Generated isolated fixture surroundings for recorded session inspection. These files are synthetic unless explicitly listed in the private import receipt.\n",
                [".git/HEAD"] = "ref: refs/heads/recorded\n",
                [".git/config"] = "[core]\n\trepositoryformatversion = 0\n\tbare = false\n"
            };
            var hashes = contents.ToDictionary(p => p.Key, p => SeedFiles.Hash(p.Value));
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var session in b.Sessions.Where(s => replicas.Single(r => r.OriginalKey == s.Key).TopId == top.Id)) foreach (var file in session.Files)
            {
                if (hashes.TryGetValue(file.RelativePath, out var h) && h != file.Sha256) throw new InvalidDataException("Conflicting fixture file destinations.");
                hashes[file.RelativePath] = file.Sha256; sources[file.RelativePath] = file.SourcePath;
            }
            result.Add(new(Path.Combine(home, "sessions", top.Id), hashes, sources));
        }
        foreach (var s in b.Sessions.Where(s => s.Attachments.Count > 0))
        {
            var hashes = new Dictionary<string, string>(); var sources = new Dictionary<string, string>();
            foreach (var a in s.Attachments)
            { if (hashes.ContainsKey(a.RelativePath)) throw new InvalidDataException("Duplicate attachment destination."); hashes[a.RelativePath] = a.Sha256; sources[a.RelativePath] = a.SourcePath; }
            result.Add(new(Path.Combine(home, "attachments", SessionId(b.Manifest.OriginId, s.Key)), hashes, sources));
        }
        return result;
    }
    private static void CheckFolderPaths(IReadOnlyList<SeedFolder> folders, bool prior)
    { foreach (var folder in folders) { SeedFiles.SafePath(folder.Path, false); if (File.Exists(folder.Path) || Directory.Exists(folder.Path) && !prior) throw new InvalidDataException("Existing asset folder collision."); } }
    private static void ValidateFolders(IReadOnlyList<SeedFolder> folders) { foreach (var f in folders) ValidateFolder(f); }
    private static void ValidateFolder(SeedFolder folder, bool allowMissing = false)
    {
        SeedFiles.SafePath(folder.Path);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(folder.Path);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                SeedFiles.SafePath(entry);
                var relative = Path.GetRelativePath(folder.Path, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    if (!folder.Files.Keys.Any(f => f.StartsWith(relative + "/", StringComparison.Ordinal)) && relative is not (".git/objects" or ".git/refs" or ".git/refs/heads"))
                        throw new InvalidDataException("User-modified imported directory tree.");
                    pending.Push(entry);
                }
                else
                {
                    if (!folder.Files.TryGetValue(relative, out var hash) || SeedFiles.HashFile(SeedFiles.Input(entry)) != hash)
                        throw new InvalidDataException("Divergent imported asset fingerprint.");
                    files.Add(relative);
                }
            }
        }
        if (!allowMissing && files.Count != folder.Files.Count) throw new InvalidDataException("User-modified/missing imported assets; refusing recovery cleanup.");
    }
    private static void Stage(SeedFolder folder, string digest, Action<string>? fault)
    {
        var stage = folder.Path + ".seed-" + digest + ".staging";
        SeedFiles.SafePath(stage, false);
        if (Directory.Exists(stage) || File.Exists(stage)) throw new InvalidDataException("Existing staging folder collision.");
        SeedFiles.PrivateDirectory(Path.GetDirectoryName(folder.Path)!); SeedFiles.PrivateDirectory(stage);
        foreach (var (relative, hash) in folder.Files)
        {
            var path = Path.Combine(stage, relative); SeedFiles.PrivateDirectory(Path.GetDirectoryName(path)!);
            if (folder.Sources.TryGetValue(relative, out var source)) File.Copy(SeedFiles.Input(source), path, false);
            else File.WriteAllText(path, relative switch { ".git/HEAD" => "ref: refs/heads/recorded\n", ".git/config" => "[core]\n\trepositoryformatversion = 0\n\tbare = false\n", _ => "Generated isolated fixture surroundings for recorded session inspection. These files are synthetic unless explicitly listed in the private import receipt.\n" });
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (var written = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) written.Flush(true);
            if (SeedFiles.HashFile(path) != hash) throw new InvalidDataException("Asset changed during preparation.");
            fault?.Invoke("staging-file");
        }
        if (folder.Files.ContainsKey(".git/HEAD")) { SeedFiles.PrivateDirectory(Path.Combine(stage, ".git", "objects")); SeedFiles.PrivateDirectory(Path.Combine(stage, ".git", "refs", "heads")); }
        SeedFiles.SyncDirectory(stage);
        Directory.Move(stage, folder.Path);
        SeedFiles.SyncDirectory(Path.GetDirectoryName(folder.Path)!);
    }
}
