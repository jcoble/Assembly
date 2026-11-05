using System.Diagnostics;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.DevSeed;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Scheduling;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Topics;
using Microsoft.Data.Sqlite;
using Seeder = Assembly.DevSeed.DevSeed;

namespace Assembly.Tests.Infrastructure.Development;

public sealed class DevSeedTests : IDisposable
{
    private const long Now = 1_800_000_000_000;
    private readonly string _scratch = Path.GetFullPath(Directory.CreateTempSubdirectory("dev-seed-").FullName);
    private readonly string _user;
    private readonly string _worktree;
    private readonly string _home;

    public DevSeedTests()
    {
        _user = Path.Combine(_scratch, "user");
        _worktree = Path.Combine(_scratch, "checkout");
        Directory.CreateDirectory(Path.Combine(_worktree, "desktop"));
        Directory.CreateDirectory(Path.Combine(_worktree, "dotnet", "Assembly.Desktop"));
        File.WriteAllText(Path.Combine(_worktree, "desktop", "Cargo.toml"), "");
        File.WriteAllText(Path.Combine(_worktree, "dotnet", "Assembly.Desktop", "Assembly.Desktop.csproj"), "<Project/>");
        Directory.CreateDirectory(_user);
        _home = SeedProfile.For(_worktree, _user);
    }

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private SeedResult Seed(string mode = SeedMode.Reset, long now = Now, string? home = null) =>
        Seeder.Run(new SeedRequest(_worktree, home ?? _home, mode, now), _user);

    // ---- Where it may write ----

    [Theory]
    [InlineData("assembly-dotnet/mac")]
    [InlineData("assembly-dotnet")]
    [InlineData("assembly-dotnet-lane/mac")]
    [InlineData("assembly-dev")]
    [InlineData("assembly-dev/worktrees")]
    [InlineData("assembly-dev/worktrees/other-0123456789abcdef")]
    [InlineData("assembly-dev-other/worktrees/checkout-0123456789abcdef")]
    [InlineData(".")]
    public void It_refuses_every_folder_but_its_own_profile(string target)
    {
        var home = Path.GetFullPath(Path.Combine(_user, target));
        Directory.CreateDirectory(home);
        var before = Directory.GetFileSystemEntries(home);

        Assert.Throws<InvalidDataException>(() => Seed(home: home));

        Assert.Equal(before, Directory.GetFileSystemEntries(home));
    }

    [Fact]
    public void It_refuses_a_path_that_walks_out_of_its_profile()
    {
        var home = _home + "/../../../assembly-dotnet/mac";
        Assert.Throws<InvalidDataException>(() => Seed(home: home));
        Assert.False(Directory.Exists(Path.Combine(_user, "assembly-dotnet")));
    }

    [Fact]
    public void It_refuses_a_profile_that_is_a_link_to_somewhere_else()
    {
        var elsewhere = Path.Combine(_user, "assembly-dotnet", "mac");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.GetDirectoryName(_home)!);
        Directory.CreateSymbolicLink(_home, elsewhere);

        Assert.Throws<InvalidDataException>(() => Seed());

        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public void It_refuses_when_the_assembly_dev_folder_is_a_link_to_somewhere_else()
    {
        var elsewhere = Path.Combine(_user, "assembly-dotnet");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(_user, "assembly-dev"), elsewhere);

        Assert.Throws<InvalidDataException>(() => Seed());

        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public void It_writes_nothing_outside_its_profile()
    {
        Seed();
        Assert.Equal(["assembly-dev"], Directory.GetFileSystemEntries(_user).Select(Path.GetFileName));
        Assert.Equal([Path.GetFileName(_home)], Directory.GetFileSystemEntries(Path.GetDirectoryName(_home)!).Select(Path.GetFileName));
        foreach (var path in Scalars("SELECT working_folder FROM session_info WHERE working_folder IS NOT NULL UNION SELECT root_path FROM projects UNION SELECT folder FROM engine_session"))
            Assert.StartsWith(_home + Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public void It_refuses_while_a_backend_holds_the_profile()
    {
        Directory.CreateDirectory(_home);
        using (HomeLock.Take(_home)) Assert.ThrowsAny<Exception>(() => Seed());
        Assert.False(File.Exists(Path.Combine(_home, "assembly.db")));
    }

    // ---- What it makes ----

    [Fact]
    public void Sessions_cover_every_provider_and_every_kind_of_row()
    {
        Seed();
        Assert.InRange(Number("SELECT COUNT(*) FROM session_info WHERE parent_session_id IS NULL"), 25, 30);
        foreach (var provider in new[] { "claude", "codex" })
        {
            Assert.True(Number("SELECT COUNT(*) FROM session_info WHERE agent='" + provider + "'") >= 8, provider);
            foreach (var kind in new[] { "user", "message", "tool", "thinking", "turn", "stats" })
                Assert.True(Number($"SELECT COUNT(*) FROM rows r JOIN session_info s USING(session_id) WHERE s.agent='{provider}' AND r.kind='{kind}'") > 0, provider + " " + kind);
        }
        foreach (var kind in new[] { "plan", "ask", "subagent", "task", "error", "context" })
            Assert.True(Number($"SELECT COUNT(*) FROM rows WHERE kind='{kind}'") > 0, kind);
        Assert.True(Number("SELECT COUNT(*) FROM tool_details WHERE diff IS NOT NULL") >= 8);
        Assert.True(Number("SELECT COUNT(*) FROM tool_details WHERE input IS NOT NULL AND output IS NOT NULL") >= 8);
        Assert.Equal(0, Number("SELECT COUNT(*) FROM session_info i WHERE (SELECT COUNT(*) FROM rows r WHERE r.session_id=i.session_id) < 4"));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM sessions s WHERE next_seq <> (SELECT MAX(seq)+1 FROM rows r WHERE r.session_id=s.session_id)"));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM session_info WHERE title IS NULL OR title=''"));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM session_info WHERE acp_session_id IS NOT NULL"));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM engine_session WHERE provider_session_id IS NOT NULL"));
    }

    [Fact]
    public void Sessions_read_as_working_ended_settled_and_waiting_on_approval()
    {
        Seed();
        Assert.True(Number("SELECT COUNT(*) FROM session_info WHERE completed_at IS NULL AND settled_at IS NULL") >= 3);
        Assert.True(Number("SELECT COUNT(*) FROM session_info WHERE completed_at IS NOT NULL AND settled_at IS NULL") >= 3);
        Assert.True(Number("SELECT COUNT(*) FROM session_info WHERE settled_at IS NOT NULL") >= 3);
        Assert.True(Number("SELECT COUNT(*) FROM engine_session WHERE state='ended'") >= 1);
        Assert.True(Number("""SELECT COUNT(*) FROM rows WHERE kind='ask' AND json_extract(payload,'$.state')='open' AND json_extract(payload,'$.kind')='permission'""") >= 1);
        Assert.True(Number("""SELECT COUNT(*) FROM rows WHERE kind='ask' AND json_extract(payload,'$.state')='answered'""") >= 1);
    }

    [Fact]
    public void A_restarted_backend_leaves_the_running_session_and_the_open_approval_as_they_look()
    {
        Seed();
        using var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics());
        var rows = RowStore.Open(db);
        var summaries = SessionSummaries.Load(rows);
        var sessions = SessionStore.Open(db, TimeProvider.System, Path.Combine(_home, "sessions"), summaries);
        var hub = new LiveHub(rows, summaries: summaries);

        var closed = RunRecovery.CloseUnfinished(rows, hub, Now, session => sessions.Get(session) is not { Machine: not null });

        Assert.Equal(0, closed);
        Assert.True(Number("""SELECT COUNT(*) FROM rows WHERE kind='error' AND json_extract(payload,'$.code')='backend_stopped'""") == 0);
        Assert.True(summaries.AnyRunOpen);
        Assert.True(Number("""SELECT COUNT(*) FROM rows WHERE kind='ask' AND json_extract(payload,'$.state')='open'""") >= 1);
    }

    [Fact]
    public void Attachments_are_real_images_the_chat_row_points_at()
    {
        Seed();
        var attached = Scalars("""SELECT json_extract(payload,'$.text') FROM rows WHERE kind='user' AND json_extract(payload,'$.text') LIKE '%Attached image: %'""");
        Assert.True(attached.Count >= 2);
        foreach (var text in attached)
            foreach (var line in text.Split('\n').Where(line => line.StartsWith("Attached image: ", StringComparison.Ordinal)))
            {
                var path = line["Attached image: ".Length..];
                Assert.StartsWith(Path.Combine(_home, "attachments") + Path.DirectorySeparatorChar, path);
                Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(path)[..4]);
            }
    }

    [Fact]
    public void Projects_are_real_git_repos_with_changes_branches_and_worktrees()
    {
        Seed();
        var roots = Scalars("SELECT root_path FROM projects WHERE removed_at IS NULL AND machine='local'");
        Assert.True(roots.Count >= 3);
        var dirty = 0; var branches = 0; var worktrees = 0;
        foreach (var root in roots)
        {
            if (Git(root, "status", "--porcelain").Length > 0) dirty++;
            branches += Git(root, "branch", "--format=%(refname:short)").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            worktrees += Git(root, "worktree", "list", "--porcelain").Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) - 1;
            Assert.Equal("", Git(root, "fsck", "--no-progress", "--connectivity-only"));
        }
        Assert.True(dirty >= 2); Assert.True(branches >= 7); Assert.True(worktrees >= 3);
        Assert.Equal(0, Number("SELECT COUNT(*) FROM session_info WHERE parent_session_id IS NULL AND machine IS NULL AND (project_id IS NULL OR working_folder IS NULL OR branch IS NULL)"));
        foreach (var folder in Scalars("SELECT folder FROM engine_session"))
            Assert.True(Directory.Exists(folder), folder);
        var inWorktrees = Scalars("SELECT working_folder FROM session_info WHERE working_folder LIKE '%/worktrees/%'");
        Assert.True(inWorktrees.Count >= 4);
    }

    [Fact]
    public void No_repo_can_reach_a_network()
    {
        Seed();
        foreach (var root in Scalars("SELECT root_path FROM projects"))
            foreach (var url in Git(root, "remote", "-v").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Assert.Contains(_home, url);
    }

    [Fact]
    public void Workflows_show_several_states_and_nothing_can_start_a_provider()
    {
        Seed();
        var states = Scalars("SELECT DISTINCT state FROM engine_run WHERE definition_hash IS NOT NULL");
        foreach (var state in new[] { "succeeded", "failed", "cancelled", "needs_me", "running" }) Assert.Contains(state, states);
        Assert.True(Number("SELECT COUNT(*) FROM engine_needs_me WHERE state='open' AND kind='workflow_gate' AND provider_request_id IS NULL") >= 1);
        Assert.True(Number("SELECT COUNT(*) FROM engine_step WHERE state='succeeded'") >= 3);
        foreach (var path in Scalars("SELECT project_root || '/' || definition_path FROM engine_run WHERE definition_hash IS NOT NULL"))
            Assert.True(File.Exists(path), path);

        Assert.Equal(0, Number("SELECT COUNT(*) FROM engine_outbox WHERE state NOT IN ('done','failed','dead')"));
        using var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics());
        var machine = EngineSchema.Open(db, Now);
        using var read = db.OpenSnapshot(default);
        Assert.Empty(WorkflowSchedule.Ready(read, machine, Now, 100));
    }

    [Fact]
    public void The_saved_test_machine_can_never_resolve()
    {
        Seed();
        var ssh = Assert.Single(Scalars("SELECT ssh FROM saved_machines"));
        Assert.EndsWith(".invalid", ssh, StringComparison.Ordinal);
        Assert.True(Number("SELECT COUNT(*) FROM session_info WHERE machine IS NOT NULL") >= 1);
    }

    [Fact]
    public void The_database_passes_its_own_checks()
    {
        Seed();
        Assert.Equal(["ok"], Scalars("PRAGMA integrity_check"));
        Assert.Empty(Scalars("SELECT \"table\" FROM pragma_foreign_key_check"));
        Assert.False(File.Exists(Path.Combine(_home, "assembly.db-wal")) && new FileInfo(Path.Combine(_home, "assembly.db-wal")).Length > 0);
        Assert.Empty(Directory.GetFiles(_home, "*.seeding*"));
    }

    // ---- Running it again ----

    [Fact]
    public void The_same_seed_comes_out_the_same()
    {
        Seed();
        var first = Dump();
        Seed();
        Assert.Equal(first, Dump());
    }

    [Fact]
    public void A_later_seed_keeps_the_same_content_and_only_moves_its_times()
    {
        Seed();
        var first = Scalars("SELECT session_id||title||(updated_at-" + Now + ") FROM session_info ORDER BY session_id");
        Seed(now: Now + 86_400_000);
        Assert.Equal(first, Scalars("SELECT session_id||title||(updated_at-" + (Now + 86_400_000) + ") FROM session_info ORDER BY session_id"));
    }

    [Fact]
    public void An_empty_profile_is_seeded_without_being_told()
    {
        Assert.Equal("seeded", Seed(SeedMode.IfEmpty).Action);
        Assert.True(Number("SELECT COUNT(*) FROM session_info") >= 25);
    }

    [Fact]
    public void A_profile_whose_app_already_made_an_empty_database_is_still_empty()
    {
        Directory.CreateDirectory(_home);
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            RowStore.Open(db);
            SessionStore.Open(db, TimeProvider.System, Path.Combine(_home, "sessions"));
            EngineSchema.Open(db, Now);
        }
        File.WriteAllText(Path.Combine(_home, "agents.json"), "[]");

        Assert.Equal("seeded", Seed(SeedMode.IfEmpty).Action);

        Assert.True(Number("SELECT COUNT(*) FROM session_info") >= 25);
    }

    [Fact]
    public void A_profile_with_someone_s_work_is_left_alone_unless_told_to_reset()
    {
        Directory.CreateDirectory(_home);
        using (var db = AppDatabase.Open(Path.Combine(_home, "assembly.db"), TimeProvider.System, new Topics()))
        {
            RowStore.Open(db);
            SessionStore.Open(db, TimeProvider.System, Path.Combine(_home, "sessions")).Create("claude", "/somewhere");
        }

        Assert.Equal("kept", Seed(SeedMode.IfEmpty).Action);
        Assert.Equal(1, Number("SELECT COUNT(*) FROM session_info"));

        Assert.Equal("seeded", Seed(SeedMode.Reset).Action);
        Assert.True(Number("SELECT COUNT(*) FROM session_info") >= 25);
        Assert.Equal(0, Number("SELECT COUNT(*) FROM session_info WHERE repo_url='/somewhere'"));
    }

    [Fact]
    public void Deleting_every_seeded_session_does_not_bring_them_back_on_the_next_launch()
    {
        Seed(SeedMode.IfEmpty);
        using (var connection = Open(writable: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE projects SET removed_at=1; PRAGMA foreign_keys=OFF; DELETE FROM session_info;";
            command.ExecuteNonQuery();
        }
        Assert.Equal("kept", Seed(SeedMode.IfEmpty).Action);
        Assert.Equal(0, Number("SELECT COUNT(*) FROM session_info"));
    }

    [Fact]
    public void Reset_keeps_installed_tools_and_throws_away_everything_else()
    {
        Seed();
        File.WriteAllText(Path.Combine(_home, "agents.json"), "[]");
        File.WriteAllText(Path.Combine(_home, "env"), "token");
        Directory.CreateDirectory(Path.Combine(_home, "adapters"));
        File.WriteAllText(Path.Combine(_home, "adapters", "tool"), "x");
        var project = Scalars("SELECT root_path FROM projects")[0];
        File.WriteAllText(Path.Combine(project, "scribble.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_home, "sessions", "left-over"));

        Seed();

        Assert.Equal("[]", File.ReadAllText(Path.Combine(_home, "agents.json")));
        Assert.Equal("token", File.ReadAllText(Path.Combine(_home, "env")));
        Assert.True(File.Exists(Path.Combine(_home, "adapters", "tool")));
        Assert.False(File.Exists(Path.Combine(project, "scribble.txt")));
        Assert.False(Directory.Exists(Path.Combine(_home, "sessions", "left-over")));
    }

    [Fact]
    public void Reset_does_not_follow_a_link_out_of_the_profile()
    {
        Seed();
        var outside = Path.Combine(_user, "keep");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_home, "projects", "link"), outside);

        Seed();

        Assert.True(File.Exists(Path.Combine(outside, "precious")));
    }

    // ---- Helpers ----

    private SqliteConnection Open(bool writable = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_home, "assembly.db"),
            Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();
        return db;
    }

    private long Number(string sql)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private List<string> Scalars(string sql)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(Convert.ToString(reader.GetValue(0))!);
        return values;
    }

    private string AllColumns(string table) => $"SELECT {string.Join(',', Scalars($"SELECT name FROM pragma_table_info('{table}')"))} FROM {table}";

    // Every row a window reads, with the engine's random machine id taken out.
    private string Dump()
    {
        var machine = Scalars("SELECT machine_id FROM engine_meta")[0];
        var lines = new List<string>();
        // The engine makes up step and command ids, so its tables compare by what they say.
        foreach (var query in new[]
        {
            AllColumns("session_info"), AllColumns("sessions"), AllColumns("rows"), AllColumns("tool_details"), AllColumns("projects"),
            AllColumns("saved_machines"), AllColumns("engine_session"),
            "SELECT id,state,title,project_id,started_ms,ended_ms,paused FROM engine_run",
            "SELECT run_id,definition_key,state,started_ms,ended_ms,outcome_reason FROM engine_step",
            "SELECT run_id,kind,text,state FROM engine_needs_me",
        })
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(query + ":" + string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i)))));
            rows.Sort(StringComparer.Ordinal);
            lines.AddRange(rows);
        }
        foreach (var root in Scalars("SELECT root_path FROM projects ORDER BY root_path"))
            lines.Add(root + Git(root, "log", "--all", "--format=%H %D %s") + Git(root, "status", "--porcelain"));
        return string.Join('\n', lines).Replace(machine, "machine", StringComparison.Ordinal);
    }

    private static string Git(string folder, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var git = Process.Start(start)!;
        var output = git.StandardOutput.ReadToEnd();
        git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return output.Trim();
    }
}
