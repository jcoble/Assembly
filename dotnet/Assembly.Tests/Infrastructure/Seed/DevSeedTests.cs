using System.Text.Json;
using Assembly.DevSeed;
using Seeder = Assembly.DevSeed.DevSeed;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Infrastructure.Topics;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Infrastructure.Seed;

public sealed class DevSeedTests : IDisposable
{
    private readonly string _root = Path.Combine(CanonicalTemp(), "assembly-seed-test-" + Guid.NewGuid().ToString("N"));
    private static string CanonicalTemp()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        var at = Path.GetPathRoot(path)!;
        foreach (var component in path[at.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(at, component);
            at = new DirectoryInfo(next).ResolveLinkTarget(true)?.FullName ?? next;
        }
        return Path.TrimEndingDirectorySeparator(at);
    }
    private readonly string _home;
    public DevSeedTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "desktop")); Directory.CreateDirectory(Path.Combine(_root, "dotnet", "Assembly.Desktop"));
        File.WriteAllText(Path.Combine(_root, "desktop", "Cargo.toml"), ""); File.WriteAllText(Path.Combine(_root, "dotnet", "Assembly.Desktop", "Assembly.Desktop.csproj"), "");
        _home = SeedFiles.Profile(_root);
    }
    public void Dispose() { if (Directory.Exists(_home)) Directory.Delete(_home, true); Directory.Delete(_root, true); }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp", name + ".jsonl");
    private string Manifest(params SeedSource[] sources)
    {
        var path = Path.Combine(_root, "manifest.json"); File.WriteAllText(path, SeedJson.Stringify(new SeedManifest(1, "synthetic-test-origin", sources.All(source => source.Format == "acp") ? "real-recorded" : "synthetic", sources))); return path;
    }
    private static SeedSource Acp(string name) => new() { Format = "acp", Path = Fixture(name), SessionKey = name, Provider = "unknown", ProviderConfidence = "uncertain", Provenance = ["existing-recorded-fixture:" + name] };
    private SeedRequest Request(string manifest, bool dry = false) => new(1, _root, _home, manifest, dry);
    private AppDatabase Database()
    {
        Directory.CreateDirectory(_home); var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics());
        RowStore.Open(db); SessionStore.Open(db, TimeProvider.System, Path.Combine(_home, "sessions")); return db;
    }
    [Fact]
    public void Real_recordings_preserve_details_and_source_bytes()
    {
        var sources = new[] { Acp("tools"), Acp("tool-fails"), Acp("error") };
        var hashes = sources.ToDictionary(s => s.Path, s => SeedFiles.HashFile(s.Path));
        var manifest = Manifest(sources);
        var bundle = SeedSources.Read(manifest);
        Assert.Equal(3, bundle.Sessions.Count); Assert.Contains(bundle.Sessions.SelectMany(s => s.Rows), r => r.Kind == "tool");
        Assert.Contains(bundle.Sessions.SelectMany(s => s.Details), d => d.Input is not null || d.Output is not null || d.Diff is not null);
        Seeder.Run(Request(manifest));
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            var rows = RowStore.Open(db);
            foreach (var session in bundle.Sessions)
                foreach (var detail in session.Details)
                {
                    var actual = rows.ToolCall(Seeder.SessionId("synthetic-test-origin", session.Key), detail.ItemId)!;
                    Assert.Equal(detail.Input, actual.Input); Assert.Equal(detail.Output, actual.Output); Assert.Equal(detail.Diff, actual.Diff); Assert.Equal(detail.Path, actual.Path);
                }
        }
        foreach (var source in sources) Assert.Equal(hashes[source.Path], SeedFiles.HashFile(source.Path));
    }
    [Fact]
    public void Run_routing_is_namespaced_and_repeat_import_is_noop()
    {
        using (var db = Database())
        {
            db.Writer.Write("original", s => s.Execute("INSERT INTO session_info(session_id,agent,repo_url,created_at,updated_at) VALUES('original','unknown','fixture',1,1);"));
            RowStore.Open(db).Commit("original", new RowBatch { Rows = { new("run-1", null, "turn", JsonSerializer.SerializeToElement(new { state = "finished" }), 1) } });
            db.Writer.Write("sentinels", s => s.Execute("CREATE TABLE sentinel(value TEXT);INSERT INTO sentinel VALUES('untouched');"));
        }
        var manifest = Manifest(Acp("tools"), Acp("tool-fails")); var report = Seeder.Run(Request(manifest));
        Assert.Equal(2, report.InsertedSessions); Assert.True(report.InsertedRows > 0);
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            var store = RowStore.Open(db); var runs = new HashSet<string>();
            foreach (var key in new[] { "tools", "tool-fails" })
            {
                var id = Seeder.SessionId("synthetic-test-origin", key); var run = Seeder.RunId("synthetic-test-origin", key, "run-1");
                Assert.True(runs.Add(run)); Assert.NotEqual("run-1", run); Assert.Equal(id, store.SessionOfRun(run));
                var stats = store.RunRows(id, run).Where(r => r.Kind == "stats").ToArray(); Assert.All(stats, r => Assert.Equal(run, r.Payload.GetProperty("runId").GetString()));
            }
            Assert.Equal("original", store.SessionOfRun("run-1"));
            Assert.Equal("untouched", db.QueryFirst("SELECT value FROM sentinel;", r => r.Text(0), default));
        }
        var again = Seeder.Run(Request(manifest)); Assert.Equal(0, again.InsertedSessions); Assert.Equal(0, again.InsertedRows); Assert.Equal(0, again.InsertedFiles);
    }
    [Theory]
    [InlineData("before-row")]
    [InlineData("before-commit")]
    [InlineData("after-publish")]
    public void Crash_boundaries_recover_without_partial_database(string phase)
    {
        using (var db = Database()) db.Writer.Write("sentinel", s => s.Execute("CREATE TABLE sentinel(value TEXT);INSERT INTO sentinel VALUES('preserved');"));
        var manifest = Manifest(Acp("tools"));
        Assert.Throws<IOException>(() => Seeder.Run(Request(manifest), fault: p => { if (p == phase) throw new IOException("injected"); }));
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            var count = db.QueryFirst("SELECT COUNT(*) FROM session_info;", r => r.Long(0), default);
            Assert.Equal(phase == "after-publish" ? 1 : 0, count); Assert.Equal("preserved", db.QueryFirst("SELECT value FROM sentinel;", r => r.Text(0), default));
        }
        Assert.Equal(phase == "after-publish" ? 0 : 1, Seeder.Run(Request(manifest)).InsertedSessions);
    }
    [Theory]
    [InlineData("staging-file")]
    [InlineData("before-row")]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    [InlineData("after-publish")]
    public void New_profile_crash_recovers_owned_partial_stage(string phase)
    {
        var manifest = Manifest(Acp("tools"));
        Assert.Throws<IOException>(() => Seeder.Run(Request(manifest), fault: p => { if (p == phase) throw new IOException("injected"); }));
        var report = Seeder.Run(Request(manifest));
        Assert.Equal(phase is "after-publish" or "after-commit" ? 0 : 1, report.InsertedSessions);
        Assert.Equal(0, Seeder.Run(Request(manifest)).InsertedRows);
    }
    [Fact]
    public void Live_home_lock_and_arbitrary_profile_are_refused()
    {
        var manifest = Manifest(Acp("tools")); Directory.CreateDirectory(_home);
        using (var held = HomeLock.Take(_home)) Assert.ThrowsAny<Exception>(() => Seeder.Run(Request(manifest)));
        Assert.Throws<InvalidDataException>(() => Seeder.Run(Request(manifest) with { ComputedHome = _root }));
    }
    [Fact]
    public void Dry_run_never_creates_profile_and_divergent_replica_is_refused()
    {
        var manifest = Manifest(Acp("tools")); Assert.Equal(0, Seeder.Run(Request(manifest, true)).InsertedRows); Assert.False(Directory.Exists(_home));
        Seeder.Run(Request(manifest));
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics())) db.Writer.Write("user-edit", s => s.Execute("UPDATE session_info SET title='my draft';"));
        Assert.Throws<InvalidDataException>(() => Seeder.Run(Request(manifest)));
    }
    private SeedSource Normalized(string key, params SeedRow[] rows)
    {
        var path = Path.Combine(_root, key + ".json"); File.WriteAllText(path, SeedJson.Stringify(rows));
        return new() { Format = "normalized", Path = path, RowFormat = 4, SessionKey = key, Provenance = ["synthetic-safety-fixture"] };
    }
    [Fact]
    public void Normalized_overlap_gaps_and_generated_run_references_are_consistent()
    {
        var source = Normalized("normalized",
            new(2, "run-1", "run-1:plan", "plan", JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() }), 10),
            new(5, "run-1", "context:run-1", "context", JsonSerializer.SerializeToElement(new { used = 1, size = 10 }), 11),
            new(7, "run-1", "run-1:1:thinking", "thinking", JsonSerializer.SerializeToElement(new { text = "synthetic", done = true }), 12),
            new(9, null, null, "user", JsonSerializer.SerializeToElement(new { text = "synthetic standalone" }), 13));
        var manifest = Manifest(source, source); var first = Seeder.Run(Request(manifest)); Assert.Equal(4, first.DeduplicatedRows); Assert.Equal(4, first.InsertedRows);
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            var id = Seeder.SessionId("synthetic-test-origin", "normalized"); var run = Seeder.RunId("synthetic-test-origin", "normalized", "run-1");
            var rows = RowStore.Open(db).RowsAfter(id, 0); Assert.Equal(new long[] { 2, 5, 7, 9 }, rows.Select(r => r.Seq));
            Assert.Equal(run + ":plan", rows[0].ItemId); Assert.Equal("context:" + run, rows[1].ItemId); Assert.Equal(run + ":1:thinking", rows[2].ItemId); Assert.Null(rows[3].RunId);
            Assert.Equal(10, db.QueryFirst("SELECT next_seq FROM sessions WHERE session_id=$id;", r => r.Long(0), default, new SqlArg("$id", id)));
        }
        Assert.Equal(0, Seeder.Run(Request(manifest)).InsertedRows);
        var conflicting = source with { Path = Path.Combine(_root, "conflict.json") };
        File.WriteAllText(conflicting.Path, SeedJson.Stringify(new[] { new SeedRow(2, "run-1", null, "user", JsonSerializer.SerializeToElement(new { text = "different" }), 10) }));
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source, conflicting)));
    }
    [Fact]
    public async Task Explicit_file_mapping_reaches_real_files_consumers_and_parent_root()
    {
        var file = Path.Combine(_root, "safe.txt"); File.WriteAllText(file, "Clearly synthetic fixture surroundings.");
        var source = Normalized("parent", new SeedRow(1, "run-1", "tool", "tool", JsonSerializer.SerializeToElement(new { name = "Edit", state = "completed", kind = "edit", path = "/historical/owner.txt", hasDetail = true, files = new[] { new { path = "/historical/owner.txt", added = 1, removed = 0 } } }), 10));
        var detail = Path.Combine(_root, "details.json"); File.WriteAllText(detail, SeedJson.Stringify(new[] { new ToolDetail("tool", "synthetic input", "synthetic output", "synthetic diff", "/historical/owner.txt") }));
        source = source with { DetailPaths = [detail], Files = [new(file, "safe.txt", SeedFiles.HashFile(file), OriginalPath: "/historical/owner.txt")] };
        var child = Normalized("child", new SeedRow(3, null, null, "user", JsonSerializer.SerializeToElement(new { text = "Synthetic child" }), 11)) with { Metadata = new(ParentSessionKey: "parent") };
        var manifest = Manifest(source, child); Seeder.Run(Request(manifest));
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            var rows = RowStore.Open(db); var sessions = SessionStore.Open(db, TimeProvider.System, Path.Combine(_home, "sessions"));
            var id = Seeder.SessionId("synthetic-test-origin", "parent"); var childId = Seeder.SessionId("synthetic-test-origin", "child");
            Assert.Equal("safe.txt", Assert.Single(rows.FilesTouched(id, default)).Path); Assert.Equal("safe.txt", rows.ToolCall(id, "tool")!.Path);
            var root = sessions.Root(sessions.Get(childId)!); Assert.Equal(Path.Combine(_home, "sessions", id), root);
            var content = await FileRead.ReadAsync(root!, "safe.txt", 0, default); Assert.Contains("synthetic", content.Content);
            await Assert.ThrowsAnyAsync<Exception>(() => FileRead.ReadAsync(root!, "/historical/owner.txt", 0, default));
        }
    }
    [Fact]
    public void Readonly_database_allowlist_skips_remote_and_preserves_hash()
    {
        var path = Path.Combine(_root, "synthetic-safe-backup.db");
        using (var db = AppDatabase.Open(path, TimeProvider.System, new Topics()))
        {
            var rows = RowStore.Open(db); SessionStore.Open(db, TimeProvider.System, Path.Combine(_root, "source-sessions"));
            db.Writer.Write("synthetic-source", scope => scope.Execute("INSERT INTO session_info(session_id,agent,repo_url,acp_session_id,created_at,updated_at,machine) VALUES('local','unknown','/historical/repo','DO-NOT-RESUME',1,2,NULL),('remote','unknown','/historical/repo','REMOTE-RESUME',1,2,'DO-NOT-CONNECT');CREATE TABLE saved_machines(secret TEXT);INSERT INTO saved_machines VALUES('DO-NOT-COPY');"));
            rows.Commit("local", new RowBatch { Rows = { new("run-1", null, "turn", JsonSerializer.SerializeToElement(new { state = "finished" }), 2) } });
        }
        var hash = SeedFiles.HashFile(path); var source = new SeedSource { Format = "database", Path = path, Provenance = ["synthetic-safety-fixture"] };
        var manifest = Manifest(source); var report = Seeder.Run(Request(manifest)); Assert.Equal(1, report.InsertedSessions); Assert.Equal(1, report.SkippedRemote); Assert.Equal(hash, SeedFiles.HashFile(path));
        using var target = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics());
        Assert.Null(target.QueryFirst("SELECT acp_session_id FROM session_info;", r => r.IsNull(0) ? null : r.Text(0), default));
        Assert.Null(target.QueryFirst("SELECT machine FROM session_info;", r => r.IsNull(0) ? null : r.Text(0), default));
        Assert.Equal(0, target.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE name='saved_machines';", r => r.Long(0), default));
        var run = Seeder.RunId("synthetic-test-origin", "local", "run-1"); Assert.Equal(Seeder.SessionId("synthetic-test-origin", "local"), RowStore.Open(target).SessionOfRun(run));
    }
    [Fact]
    public void Mixed_database_provider_claims_remain_truthful()
    {
        var path = Path.Combine(_root, "mixed-provider-backup.db");
        using (var db = AppDatabase.Open(path, TimeProvider.System, new Topics()))
        {
            RowStore.Open(db); SessionStore.Open(db, TimeProvider.System, Path.Combine(_root, "source-sessions"));
            db.Writer.Write("mixed-synthetic-providers", scope => scope.Execute("INSERT INTO session_info(session_id,agent,repo_url,created_at,updated_at) VALUES('a','claude','/historical',1,2),('b','codex','/historical',1,2);"));
        }
        var source = new SeedSource { Format = "database", Path = path, Provider = "claude", ProviderConfidence = "verified", Provenance = ["synthetic-safety-fixture"] };
        var bundle = SeedSources.Read(Manifest(source));
        Assert.Equal("claude", bundle.Sessions.Single(s => s.Key == "a").Provider);
        var conflicting = bundle.Sessions.Single(s => s.Key == "b");
        Assert.Equal("unknown", conflicting.Provider); Assert.Equal("conflicting", conflicting.ProviderConfidence);
        Assert.Equal("codex", conflicting.Metadata.OriginalProvider); Assert.Contains("stored:codex", conflicting.Metadata.ProviderConflicts!);
    }
    [Theory]
    [InlineData("user", "{}")]
    [InlineData("message", "{}")]
    [InlineData("turn", "{\"state\":\"invented\"}")]
    [InlineData("tool", "{\"name\":\"tool\",\"state\":\"completed\"}")]
    public void Required_normalized_payload_fields_are_validated(string kind, string json)
    {
        var source = Normalized("invalid", new SeedRow(1, null, null, kind, JsonDocument.Parse(json).RootElement.Clone(), 1));
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source)));
    }
    [Theory]
    [InlineData("before-commit")]
    [InlineData("after-publish")]
    public void Incremental_overlap_preserves_prior_import_and_unrelated_draft(string phase)
    {
        var first = Manifest(Acp("tools")); Seeder.Run(Request(first));
        var priorId = Seeder.SessionId("synthetic-test-origin", "tools");
        var priorFile = Path.Combine(_home, "sessions", priorId, "SEED-FIXTURE.txt"); var priorHash = SeedFiles.HashFile(priorFile);
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
            db.Writer.Write("unrelated-draft", scope => scope.Execute("CREATE TABLE draft_sentinel(value TEXT);INSERT INTO draft_sentinel VALUES('preserve my draft');"));
        var combined = Manifest(Acp("tools"), Acp("tool-fails"));
        Assert.Throws<IOException>(() => Seeder.Run(Request(combined), fault: p => { if (p == phase) throw new IOException("injected"); }));
        Assert.Equal(priorHash, SeedFiles.HashFile(priorFile));
        var recovered = Seeder.Run(Request(combined)); Assert.Equal(phase == "after-publish" ? 0 : 1, recovered.InsertedSessions);
        Assert.Equal(0, Seeder.Run(Request(combined)).InsertedRows);
        using var final = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics());
        Assert.Equal(2, final.QueryFirst("SELECT COUNT(*) FROM session_info;", row => row.Long(0), default));
        Assert.Equal("preserve my draft", final.QueryFirst("SELECT value FROM draft_sentinel;", row => row.Text(0), default));
    }
    [Fact]
    public void Attachment_reference_is_rewritten_exactly_and_private_receipt_has_no_transcript()
    {
        var attachmentRoot = Path.Combine(_root, "approved-images"); Directory.CreateDirectory(attachmentRoot);
        var name = "11111111-1111-4111-8111-111111111111.png"; var image = Path.Combine(attachmentRoot, name);
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOcQAAAAASUVORK5CYII="));
        var source = Normalized("image", new SeedRow(1, null, null, "user", JsonSerializer.SerializeToElement(new { text = "Synthetic message body must stay out of receipt.\nAttached image: /historical/image.png\nThis mentions /historical/image.png in ordinary text." }), 10));
        source = source with { AttachmentRoot = attachmentRoot, Attachments = [new(image, name, SeedFiles.HashFile(image), OriginalPath: "/historical/image.png")] };
        var report = Seeder.Run(Request(Manifest(source))); Assert.Equal(1, report.Attachments);
        var id = Seeder.SessionId("synthetic-test-origin", "image");
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            var text = Assert.Single(RowStore.Open(db).RowsAfter(id, 0)).Payload.GetProperty("text").GetString();
            Assert.Contains("Attached image: " + Path.Combine(_home, "attachments", id, name), text);
            Assert.Contains("mentions /historical/image.png in ordinary text", text);
        }
        Assert.DoesNotContain("Synthetic message body", File.ReadAllText(report.ReceiptPath!));
        Assert.Equal(SeedFiles.HashFile(image), SeedFiles.HashFile(Path.Combine(_home, "attachments", id, name)));
    }
    [Fact]
    public void Asset_source_credentials_symlinks_hardlinks_and_hash_mismatch_are_refused()
    {
        var credentials = Path.Combine(_root, "id_rsa"); File.WriteAllText(credentials, "synthetic credential sentinel");
        Assert.Throws<InvalidDataException>(() => SeedFiles.CheckAsset(new(credentials, "safe.txt", SeedFiles.HashFile(credentials)), false, null));
        var safe = Path.Combine(_root, "safe.txt"); File.WriteAllText(safe, "synthetic");
        Assert.Throws<InvalidDataException>(() => SeedFiles.CheckAsset(new(safe, "safe.txt", new string('0', 64)), false, null));
        var link = Path.Combine(_root, "link.txt"); File.CreateSymbolicLink(link, safe);
        Assert.Throws<InvalidDataException>(() => SeedFiles.Input(link));
        var hard = Path.Combine(_root, "hard.txt");
        var start = new System.Diagnostics.ProcessStartInfo("ln") { UseShellExecute = false }; start.ArgumentList.Add(safe); start.ArgumentList.Add(hard);
        using (var process = System.Diagnostics.Process.Start(start)!) { process.WaitForExit(); Assert.Equal(0, process.ExitCode); }
        Assert.Throws<InvalidDataException>(() => SeedFiles.Input(hard));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void Unsupported_database_versions_fail_readonly(int version)
    {
        var path = Path.Combine(_root, "unsupported.db");
        using (var db = new SqliteConnection("Data Source=" + path)) { db.Open(); using var command = db.CreateCommand(); command.CommandText = $"PRAGMA user_version={version};"; command.ExecuteNonQuery(); }
        var hash = SeedFiles.HashFile(path); var source = new SeedSource { Format = "database", Path = path, Provenance = ["synthetic-safety-fixture"] };
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source))); Assert.Equal(hash, SeedFiles.HashFile(path));
    }
    [Fact]
    public void Many_source_real_recordings_report_unique_counts_and_precise_rejections()
    {
        var accepted = new List<SeedSource>(); var rejected = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp"), "*.jsonl"))
        {
            var source = Acp(Path.GetFileNameWithoutExtension(file));
            try { SeedSources.Read(Manifest(source)); accepted.Add(source); }
            catch (InvalidDataException error) { Assert.Contains(source.Path, error.Message); rejected.Add(Path.GetFileNameWithoutExtension(file)); }
        }
        Assert.Equal(new[] { "background", "background-async", "steer-background" }, rejected.Order(StringComparer.Ordinal));
        var bundle = SeedSources.Read(Manifest(accepted.ToArray())); Assert.Equal(11, bundle.Sessions.Count);
        var report = Seeder.Run(Request(Manifest(accepted.ToArray()), true)); Assert.Equal(11, report.UniqueSessions); Assert.Equal(11, report.RealSessions); Assert.Equal(0, report.SyntheticSessions);
    }
    [Theory]
    [InlineData("normalized")]
    [InlineData("database")]
    public void Independent_stored_sources_namespace_reused_runs_and_preserve_provider_items(string format)
    {
        var records = new[]{
            new SeedRow(1,"run-1","run-1:plan","plan",JsonSerializer.SerializeToElement(new{items=Array.Empty<object>()}),1),
            new SeedRow(3,"run-1","context:run-1","context",JsonSerializer.SerializeToElement(new{used=1,size=10}),2),
            new SeedRow(5,"run-1","run-1:1:thinking","thinking",JsonSerializer.SerializeToElement(new{text="synthetic",done=true}),3),
            new SeedRow(7,"run-1",null,"stats",JsonSerializer.SerializeToElement(new{runId="run-1",status="completed",toolCalls=1}),4),
            new SeedRow(9,"run-1","run-1:provider-tool","tool",JsonSerializer.SerializeToElement(new{name="synthetic",state="completed",hasDetail=false}),5),
            new SeedRow(11,"run-1",null,"turn",JsonSerializer.SerializeToElement(new{state="finished"}),6),
            new SeedRow(13,null,null,"user",JsonSerializer.SerializeToElement(new{text="synthetic standalone"}),7)};
        SeedSource[] sources;
        if (format == "normalized") sources = [Normalized("source-a", records), Normalized("source-b", records)];
        else
        {
            var path = Path.Combine(_root, "stored-run-backup.db");
            using (var db = AppDatabase.Open(path, TimeProvider.System, new Topics()))
            {
                var rows = RowStore.Open(db); SessionStore.Open(db, TimeProvider.System, Path.Combine(_root, "source-sessions"));
                db.Writer.Write("synthetic-stored-sessions", scope => scope.Execute("INSERT INTO session_info(session_id,agent,repo_url,created_at,updated_at) VALUES('source-a','unknown','/historical',1,7),('source-b','unknown','/historical',1,7);"));
                foreach (var key in new[] { "source-a", "source-b" })
                {
                    var batch = new RowBatch(); batch.Rows.AddRange(records.Select(row => new NewRow(row.RunId, row.ItemId, row.Kind, row.Payload, row.CreatedAt))); rows.Commit(key, batch);
                }
            }
            sources = [new() { Format = "database", Path = path, Provenance = ["synthetic-safety-fixture"] }];
        }
        using (var db = Database())
        {
            db.Writer.Write("unrelated-run", scope => scope.Execute("INSERT INTO session_info(session_id,agent,repo_url,created_at,updated_at) VALUES('original','unknown','fixture',1,1);"));
            RowStore.Open(db).Commit("original", new RowBatch { Rows = { new("run-1", null, "turn", JsonSerializer.SerializeToElement(new { state = "finished" }), 1) } });
        }
        var manifest = Manifest(sources); Seeder.Run(Request(manifest));
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            var rows = RowStore.Open(db); var distinct = new HashSet<string>();
            foreach (var key in new[] { "source-a", "source-b" })
            {
                var id = Seeder.SessionId("synthetic-test-origin", key); var run = Seeder.RunId("synthetic-test-origin", key, "run-1"); Assert.True(distinct.Add(run)); Assert.Equal(id, rows.SessionOfRun(run));
                var stored = rows.RowsAfter(id, 0); Assert.Equal(run + ":plan", stored.Single(row => row.Kind == "plan").ItemId);
                Assert.Equal("context:" + run, stored.Single(row => row.Kind == "context").ItemId); Assert.Equal(run + ":1:thinking", stored.Single(row => row.Kind == "thinking").ItemId);
                Assert.Equal(run, stored.Single(row => row.Kind == "stats").Payload.GetProperty("runId").GetString());
                Assert.Equal("run-1:provider-tool", stored.Single(row => row.Kind == "tool").ItemId); Assert.Null(stored.Single(row => row.Kind == "user").RunId);
            }
            Assert.Equal("original", rows.SessionOfRun("run-1"));
        }
        Assert.Equal(0, Seeder.Run(Request(manifest)).InsertedRows);
        foreach (var source in sources.Where(source => source.Format == "database"))
        { Assert.False(File.Exists(source.Path + "-wal")); Assert.False(File.Exists(source.Path + "-shm")); }
    }
    [Theory]
    [InlineData("runId")]
    [InlineData("itemId")]
    public void Normalized_nullable_fields_must_be_explicit(string missing)
    {
        var source = Normalized("omitted", new SeedRow(1, null, null, "user", JsonSerializer.SerializeToElement(new { text = "synthetic" }), 1));
        var array = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(source.Path))!.AsArray(); array[0]!.AsObject().Remove(missing); File.WriteAllText(source.Path, array.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source)));
    }
    [Theory]
    [InlineData(".npmrc")]
    [InlineData(".netrc")]
    [InlineData(".env")]
    [InlineData(".pypirc")]
    public void Known_credential_source_files_cannot_be_renamed_safe(string name)
    {
        var path = Path.Combine(_root, name); File.WriteAllText(path, "synthetic credential sentinel");
        Assert.Throws<InvalidDataException>(() => SeedFiles.CheckAsset(new(path, "safe.txt", SeedFiles.HashFile(path)), false, null));
    }
    [Fact]
    public void Invalid_task_state_is_not_an_archival_terminal_outcome()
    {
        var source = Normalized("task", new SeedRow(1, null, "task:1", "task", JsonSerializer.SerializeToElement(new { name = "synthetic", state = "garbage", canStop = false }), 1));
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source)));
    }
    [Fact]
    public void Overlapping_replica_provenance_associates_each_artifact_hash()
    {
        var first = Normalized("overlap-a", new SeedRow(1, null, null, "user", JsonSerializer.SerializeToElement(new { text = "synthetic private message" }), 1)) with { SessionKey = "same", Provenance = ["capture-a"] };
        var second = first with { Path = Path.Combine(_root, "overlap-b.json"), Provenance = ["capture-b"] }; File.Copy(first.Path, second.Path);
        var report = Seeder.Run(Request(Manifest(first, second))); var receipt = SeedJson.Parse<SeedReceipt>(File.ReadAllText(report.ReceiptPath!));
        var replica = Assert.Single(receipt.Replicas); Assert.Contains("capture-a", replica.Provenance); Assert.Contains("capture-b", replica.Provenance);
        foreach (var source in new[] { first, second }) Assert.Contains(replica.SourceInputs, input => input.Path == SeedFiles.Input(source.Path) && input.Sha256 == SeedFiles.HashFile(source.Path));
        Assert.Equal(1, report.UniqueSessions); Assert.Equal(1, report.DeduplicatedRows);
    }
    [Fact]
    public void Canonical_existing_ancestors_and_physical_overlap_are_checked()
    {
        var source = Path.Combine(_root, "physical.txt"); File.WriteAllText(source, "synthetic");
        Assert.Equal(Path.Combine(_root, "future", "file.txt"), SeedFiles.SafePath(Path.Combine(_root, "future", "file.txt"), false));
        Assert.True(SeedFiles.OverlapsHome([source], _root)); Assert.False(SeedFiles.OverlapsHome([source], _home));
        if (OperatingSystem.IsMacOS())
        {
            var caseAlias = source.ToUpperInvariant();
            if (File.Exists(caseAlias)) Assert.True(SeedFiles.OverlapsHome([caseAlias], _root));
            var dataAlias = "/System/Volumes/Data" + source;
            if (File.Exists(dataAlias)) Assert.True(SeedFiles.OverlapsHome([dataAlias], _root));
            Console.WriteLine($"Mac case alias exercised: {File.Exists(caseAlias)}; Data firmlink alias exercised: {File.Exists(dataAlias)}");
        }
        else Console.WriteLine("Mac case/firmlink alias branches not exercised on this platform.");
    }
    [Fact]
    public void Missing_existing_target_table_fails_without_writer_housekeeping()
    {
        using (var db = Database()) db.Writer.Write("missing-schema", s => s.Execute("DROP TABLE session_commands;"));
        var path = Path.Combine(_home, "assembly.db"); var hash = SeedFiles.HashFile(path);
        Assert.Throws<InvalidDataException>(() => Seeder.Run(Request(Manifest(Acp("tools")))));
        Assert.Equal(hash, SeedFiles.HashFile(path));
    }
    [Fact]
    public void Missing_detail_unknown_format_unsafe_paths_and_unfinished_sources_fail()
    {
        var path = Path.Combine(_root, "rows.json");
        File.WriteAllText(path, SeedJson.Stringify(new[] { new SeedRow(9, "run-1", "tool", "tool", JsonSerializer.SerializeToElement(new { name = "edit", state = "completed", hasDetail = true }), 1) }));
        var source = new SeedSource { Format = "normalized", Path = path, SessionKey = "original", RowFormat = 4, Provenance = ["synthetic-safety-fixture"] };
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source)));
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source with { RowFormat = 5 })));
        Assert.Throws<InvalidDataException>(() => SeedFiles.Relative("../owner"));
        Assert.Throws<InvalidDataException>(() => SeedFiles.Relative(".git/config"));
        File.WriteAllText(path, SeedJson.Stringify(new[] { new SeedRow(1, "run-1", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 1) }));
        Assert.Throws<InvalidDataException>(() => SeedSources.Read(Manifest(source)));
    }
}
