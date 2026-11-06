using System.Diagnostics;
using System.Text.Json;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

/// <summary>The helper on real processes and a real rename, with shell scripts as the two versions of the app.</summary>
public sealed partial class AppSwapTests : IDisposable
{
    private const string Silent = "sleep 300 & echo $! > \"$HOME_DIR/backend.lock.pid\"; echo $! > \"$HOME_DIR/its-backend.pid\"; exec sleep 300";
    private readonly string _root = Directory.CreateTempSubdirectory("appswap-").FullName;
    private readonly List<Process> _started = [];
    private string App => Path.Combine(_root, "Assembly.app");
    private string Stage => Path.Combine(_root, ".assembly-update-2.0.0");
    private string Fresh => Path.Combine(Stage, "new/Assembly.app");
    private string Home => Path.Combine(_root, "home");
    private string Folder => Path.Combine(Home, "app-update");
    private string Database => Path.Combine(Home, "assembly.db");
    private string Backup => Path.Combine(Folder, "assembly-before-2.0.0.db");

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_root, "*.pid", SearchOption.AllDirectories))
            if (int.TryParse(File.ReadAllText(file).Trim(), out var pid) && pid != Environment.ProcessId) StopIfRunning(pid);
        foreach (var process in _started) { if (!process.HasExited) process.Kill(); process.Dispose(); }
        Directory.Delete(_root, recursive: true);
    }

    private static void StopIfRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); process.Kill(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
    }

    private void Bundle(string path, string name, string body)
    {
        var program = Path.Combine(path, "Contents/MacOS/assembly-desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(Path.Combine(path, "version"), name);
        File.WriteAllText(program, $"#!/bin/sh\nHOME_DIR='{Home}'\nFOLDER='{Folder}'\necho $$ > \"$HOME_DIR/{name}.pid\"\n{body}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, (UnixFileMode)0b111_101_101);
    }

    private Process Sleeper()
    {
        var process = Process.Start(new ProcessStartInfo("sleep", "300") { UseShellExecute = false })!;
        _started.Add(process);
        return process;
    }

    private (AppSwapPlan Plan, Process Window, Process Backend) Prepared(string freshBody, int acknowledge = 30, int quit = 30)
    {
        Directory.CreateDirectory(Folder);
        Bundle(App, "old", "exec sleep 300");
        Bundle(Fresh, "new", freshBody);
        File.WriteAllText(Database, "original");
        var (window, backend) = (Sleeper(), Sleeper());
        return (new(App, Fresh, "Contents/MacOS/assembly-desktop", Stage, Folder, "2.0.0", "1.0.0", Database, Backup,
            window.Id, AppSwap.StartOf(window.Id), backend.Id, AppSwap.StartOf(backend.Id), quit, acknowledge), window, backend);
    }

    private string Acknowledges => $"cat <<'PY' | python3 -\nimport socket\ns=socket.socket(socket.AF_UNIX)\ns.connect('{Folder}/ack.sock')\ns.send(b'2.0.0')\nPY\nexec sleep 300";

    private string Running(string name) => File.ReadAllText(Path.Combine(App, "version")) == name ? name : "other";

    private string? SaidNote() => JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Folder, "failed.json")), AppSwapJson.Default.AppSwapResult)?.Note;

    // A person may have used the new version before it went back, so its database is kept and the note says where.
    private string WentBackKeepingLaterWrites =>
        $"Assembly 2.0.0 didn't start, so this app went back to 1.0.0. Anything written after the update is kept in {Database}.failed-2.0.0, not in this app's database.";

    private static void End(Process process) { process.Kill(); process.WaitForExit(); }

    private async Task<int> Started(string name)
    {
        var file = Path.Combine(Home, name + ".pid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(file) || File.ReadAllText(file).Trim().Length == 0) await Task.Delay(20, deadline.Token);
        return int.Parse(File.ReadAllText(file).Trim());
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task The_helper_times_each_of_its_stages_in_the_update_log()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Acknowledges);
        End(window); End(backend);

        Assert.Equal(0, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(["old app quit", "backup", "swap", "first start"], AppUpdateTests.TimedStages(Folder));
    }

    [Fact]
    public async Task It_waits_for_exactly_the_old_app_then_swaps_and_finishes_once_the_new_one_answers()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Acknowledges);
        var running = AppSwap.RunAsync(plan);
        await Task.WhenAny(running, Task.Delay(300));
        Assert.False(running.IsCompleted);
        Assert.Equal("old", Running("old"));
        End(window);
        await Task.WhenAny(running, Task.Delay(300));
        Assert.False(running.IsCompleted);
        Assert.Equal("old", Running("old"));
        End(backend);

        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("new", Running("new"));
        Assert.True(Alive(await Started("new")));
        Assert.False(Directory.Exists(Stage));
        Assert.False(File.Exists(Path.Combine(Folder, "failed.json")));
        Assert.Equal("original", File.ReadAllText(Database));
        Assert.Empty(Directory.GetFiles(Folder, AppSwap.BackupPrefix + "*"));
    }

    [Fact]
    public async Task A_new_version_that_exits_puts_the_old_app_and_database_back_and_says_so()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo migrated > '{Database}'\nexit 3");
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("old", Running("old"));
        Assert.Equal("original", File.ReadAllText(Database));
        Assert.Equal("migrated\n", File.ReadAllText(Database + ".failed-2.0.0"));
        Assert.Equal(WentBackKeepingLaterWrites, SaidNote());
        Assert.True(Alive(await Started("old")));
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task Going_back_loses_nothing_written_right_up_to_the_quit_and_keeps_every_versions_kept_writes()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo migrated > '{Database}'\nrm -f '{Database}-wal'\nexit 3");
        File.WriteAllText(Path.Combine(Folder, "assembly-before-1.0.0.db"), "an older copy");
        File.WriteAllText(Database + ".failed-1.5.0", "an older failure");
        // A turn's last rows, after the plan was written. The real database below has them in its log as well.
        File.WriteAllText(Database, "written late");
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("written late", File.ReadAllText(Database));
        Assert.Equal([Path.GetFileName(Backup)], Directory.GetFiles(Folder, "assembly-before-*").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("an older failure", File.ReadAllText(Database + ".failed-1.5.0"));
        Assert.True(File.Exists(Database + ".failed-2.0.0"));
    }

    [Fact]
    public async Task A_real_database_with_writes_only_in_its_log_comes_back_whole_after_a_migration_that_failed()
    {
        if (!OperatingSystem.IsLinux()) return;
        // A new version that migrates for real, leaving a table the old one has never seen, then dies.
        var migrate = $"python3 -c \"import sqlite3; d=sqlite3.connect('{Database}'); d.execute('create table migrated(n)'); d.execute('delete from turns'); d.commit(); d.close()\"\nexit 3";
        var (plan, window, backend) = Prepared(migrate);
        File.Delete(Database);
        // What the old app leaves when it's killed: rows only in the log, never checkpointed, with its index beside them.
        Python($$"""
            import os, sqlite3
            d = sqlite3.connect(r'{{Database}}')
            d.execute('PRAGMA journal_mode=WAL')
            d.execute('PRAGMA wal_autocheckpoint=0')
            d.execute('create table turns(n)')
            d.executemany('insert into turns values (?)', [(n,) for n in range(1000)])
            d.commit()
            os._exit(0)
            """);
        Assert.True(new FileInfo(Database + "-wal").Length > 0);
        Assert.True(File.Exists(Database + "-shm"));
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(File.Exists(Backup + "-wal"));
        Assert.False(File.Exists(Backup + "-shm"));
        // The migration did run, and what it left is kept aside.
        Assert.True(File.Exists(Database + ".failed-2.0.0"));
        Assert.False(File.Exists(Database + "-shm.partial"));
        using var opened = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database};Pooling=False");
        opened.Open();
        Assert.Equal(1000L, Scalar(opened, "select count(*) from turns"));
        Assert.Equal(499500L, Scalar(opened, "select sum(n) from turns"));
        Assert.Equal(0L, Scalar(opened, "select count(*) from sqlite_master where name = 'migrated'"));
        Assert.Equal("ok", Scalar(opened, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task The_kept_database_the_note_names_holds_the_new_versions_writes_on_its_own()
    {
        if (!OperatingSystem.IsLinux()) return;
        // The new version writes and dies with its rows only in the log, the way a stopped backend leaves them.
        var writes = $"python3 -c \"import os, sqlite3; d=sqlite3.connect('{Database}'); d.execute('PRAGMA wal_autocheckpoint=0'); d.execute('create table after_update(n)'); d.executemany('insert into after_update values (?)', [(n,) for n in range(250)]); d.commit(); os._exit(0)\"\nexit 3";
        var (plan, window, backend) = Prepared(writes);
        File.Delete(Database);
        Python($$"""
            import sqlite3
            d = sqlite3.connect(r'{{Database}}')
            d.execute('PRAGMA journal_mode=WAL')
            d.execute('create table turns(n)')
            d.executemany('insert into turns values (?)', [(n,) for n in range(1000)])
            d.commit()
            d.close()
            """);
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        var kept = Database + ".failed-2.0.0";
        Assert.Equal(WentBackKeepingLaterWrites, SaidNote());
        Assert.Equal([kept], Directory.GetFiles(Home, "*.failed-*"));
        Assert.False(File.Exists(kept + "-wal"));
        var alone = Path.Combine(Directory.CreateTempSubdirectory("kept-").FullName, "kept.db");
        File.Copy(kept, alone);
        using var opened = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={alone};Pooling=False");
        opened.Open();
        Assert.Equal(250L, Scalar(opened, "select count(*) from after_update"));
        Assert.Equal(1000L, Scalar(opened, "select count(*) from turns"));
    }

    private static object? Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Python(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("python3", ["-c", script.Replace("\n            ", "\n", StringComparison.Ordinal)]) { UseShellExecute = false })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task A_way_back_that_is_stuck_says_where_the_old_app_is_opens_what_is_there_and_deletes_nothing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var stuck = Path.Combine(Stage, "new/kept");
        // The new version takes the path the old app was moved to, so the swap back can't happen.
        var (plan, window, backend) = Prepared($"mv '{Fresh}' '{stuck}' 2>/dev/null\nexit 3");
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal($"Assembly 2.0.0 didn't start, and 1.0.0 couldn't be put back. It's kept in {Fresh}.", SaidNote());
        Assert.Equal("old", File.ReadAllText(Path.Combine(stuck, "version")));
        Assert.Equal("new", Running("new"));
        Assert.Equal("original", File.ReadAllText(Database));
    }

    private const string CheckProgram = "Contents/Resources/backend/check";
    private string Mark => Path.Combine(Folder, AppSwap.InstalledMark);

    // The new version's check, beside its window program, which writes new.pid if anything opens it.
    private void Checks(string body)
    {
        var program = Path.Combine(Fresh, CheckProgram);
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, $"#!/bin/sh\n[ \"$1\" = check-start ] && [ \"$2\" = '{Database}' ] || exit 9\n{body}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, (UnixFileMode)0b111_101_101);
    }

    [Fact]
    public async Task After_a_quit_the_new_version_goes_in_its_check_passes_and_no_window_opens()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Acknowledges);
        Checks($"echo migrated > '{Database}'\nexit 0");
        End(window); End(backend);

        Assert.Equal(0, await AppSwap.RunAsync(plan with { Check = CheckProgram }).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("new", Running("new"));
        Assert.False(File.Exists(Path.Combine(Home, "new.pid")));
        Assert.False(File.Exists(Path.Combine(Home, "old.pid")));
        Assert.Equal("migrated\n", File.ReadAllText(Database));
        Assert.False(File.Exists(Path.Combine(Folder, "failed.json")));
        // Only the next launch saying its window is up lets these go.
        Assert.Equal("old", File.ReadAllText(Path.Combine(Fresh, "version")));
        Assert.Equal("original", File.ReadAllText(Backup));
        Assert.True(File.Exists(Mark));
    }

    [Fact]
    public async Task After_a_quit_a_check_that_fails_puts_the_old_app_and_database_back_and_opens_nothing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Acknowledges);
        Checks($"echo migrated > '{Database}'\nexit 3");
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan with { Check = CheckProgram }).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("old", Running("old"));
        Assert.Equal("original", File.ReadAllText(Database));
        Assert.Equal("Assembly 2.0.0 didn't start, so this app went back to 1.0.0.", SaidNote());
        Assert.False(File.Exists(Path.Combine(Home, "new.pid")));
        Assert.False(File.Exists(Path.Combine(Home, "old.pid")));
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task A_new_version_that_never_answers_is_stopped_by_its_own_ids_and_the_old_app_comes_back()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Silent, acknowledge: 2);
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal("old", Running("old"));
        Assert.False(Alive(await Started("new")));
        Assert.False(Alive(int.Parse(File.ReadAllText(Path.Combine(Home, "its-backend.pid")).Trim())));
        Assert.True(Alive(await Started("old")));
    }

    [Fact]
    public async Task An_old_app_that_will_not_quit_keeps_running_with_nothing_swapped()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, _) = Prepared(Acknowledges, quit: 1);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("old", Running("old"));
        Assert.False(window.HasExited);
        Assert.False(Directory.Exists(Stage));
        Assert.False(File.Exists(Path.Combine(Home, "new.pid")));
        Assert.Equal("Assembly 1.0.0 didn't quit, so 2.0.0 wasn't installed.", SaidNote());
    }

    [Fact]
    public async Task A_reused_process_id_is_not_mistaken_for_the_old_app()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, _, _) = Prepared(Acknowledges);
        var other = plan with { WindowStart = plan.WindowStart - TimeSpan.TicksPerHour, BackendStart = plan.BackendStart - TimeSpan.TicksPerHour };
        Assert.Equal(0, await AppSwap.RunAsync(other).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("new", Running("new"));
    }

    [Fact]
    public void With_no_update_waiting_a_start_says_nothing_to_anyone()
    {
        AppSwap.Acknowledge(Path.Combine(_root, "nowhere"), "1.0.0");
    }

    [Fact]
    public async Task A_swap_that_cannot_happen_brings_the_old_app_back_and_says_so()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Acknowledges);
        End(window); End(backend);
        Directory.Delete(Fresh, recursive: true);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("old", Running("old"));
        Assert.Equal("Assembly 2.0.0 couldn't be put in place, so this app stayed on 1.0.0.", SaidNote());
        Assert.True(Alive(await Started("old")));
    }

    private async Task Appears(string name)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(Home, name))) await Task.Delay(20, deadline.Token);
    }

    [Fact]
    public async Task While_the_helper_works_a_reopened_backend_stops_with_home_in_use()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Acknowledges);
        Checks($"touch '{Home}/checking'\nwhile [ ! -e '{Home}/go' ]; do sleep 0.05; done\nexit 0");
        End(window); End(backend);

        var running = AppSwap.RunAsync(plan with { Check = CheckProgram });
        await Appears("checking");

        Assert.Contains("home in use", Assert.Throws<HomeInUseException>(() => HomeLock.Take(Home)).Message, StringComparison.Ordinal);
        Assert.True(Assert.Throws<HomeInUseException>(() => HomeLock.Take(Home)).Update);
        File.WriteAllText(Path.Combine(Home, "go"), "");
        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(30)));
        using var free = HomeLock.Take(Home);
    }

    [Fact]
    public async Task A_helper_that_finds_the_app_reopened_touches_nothing_and_goes_away()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared(Acknowledges);
        Checks($"echo migrated > '{Database}'\nexit 0");
        End(window); End(backend);
        using var reopened = HomeLock.Take(Home);

        Assert.Equal(1, await AppSwap.RunAsync(plan with { Check = CheckProgram }).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("old", Running("old"));
        Assert.Equal("original", File.ReadAllText(Database));
        Assert.False(File.Exists(Backup));
        Assert.False(File.Exists(Mark));
        Assert.False(File.Exists(Path.Combine(Folder, "failed.json")));
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task On_a_press_the_new_window_gets_the_home_the_helper_held()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared("flock -n \"$HOME_DIR/backend.lock\" true || exit 7\n" + Acknowledges);
        End(window); End(backend);

        Assert.Equal(0, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("new", Running("new"));
    }

    [Fact]
    public async Task A_new_version_silent_before_its_backend_starts_never_has_the_helper_stop_itself()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared("exec sleep 300", acknowledge: 1);
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal("old", Running("old"));
        Assert.True(Alive(await Started("old")));
    }

    [Fact]
    public async Task A_going_back_with_the_app_opened_meanwhile_leaves_its_database_alone_and_says_why()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo migrated > '{Database}'\n{Silent}", acknowledge: 3);
        End(window); End(backend);

        var running = AppSwap.RunAsync(plan);
        await Started("new");
        using var reopened = HomeLock.Take(Home);

        Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal("migrated\n", File.ReadAllText(Database));
        Assert.Contains("open again", SaidNote(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Stage));
    }

    // An install on quit, up to where the app stays closed: what the next launch finds.
    private async Task<AppSwapPlan> InstalledOnQuit(int acknowledge = 30)
    {
        var (plan, window, backend) = Prepared(Acknowledges, acknowledge);
        Checks($"echo migrated > '{Database}'\nexit 0");
        End(window); End(backend);
        Assert.Equal(0, await AppSwap.RunAsync(plan with { Check = CheckProgram }).WaitAsync(TimeSpan.FromSeconds(30)));
        var (nextWindow, nextBackend) = (Sleeper(), Sleeper());
        return plan with { Watch = true, WindowPid = nextWindow.Id, WindowStart = AppSwap.StartOf(nextWindow.Id), BackendPid = nextBackend.Id, BackendStart = AppSwap.StartOf(nextBackend.Id) };
    }

    private Process Own(int pid) => _started.Single(process => process.Id == pid);

    [Fact]
    public async Task The_next_launch_saying_its_window_is_up_lets_the_old_app_and_the_copy_go()
    {
        if (!OperatingSystem.IsLinux()) return;
        var watch = await InstalledOnQuit();

        var running = AppSwap.RunAsync(watch);
        await Task.WhenAny(running, Task.Delay(300));
        Assert.False(running.IsCompleted);
        AppSwap.Acknowledge(Folder, "2.0.0");

        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("new", Running("new"));
        Assert.Equal("migrated\n", File.ReadAllText(Database));
        Assert.False(Directory.Exists(Stage));
        Assert.False(File.Exists(Backup));
        Assert.False(File.Exists(Mark));
        Assert.False(File.Exists(Path.Combine(Folder, "failed.json")));
        Assert.False(Own(watch.WindowPid).HasExited);
    }

    [Fact]
    public async Task A_window_that_came_up_before_the_watch_started_still_counts()
    {
        if (!OperatingSystem.IsLinux()) return;
        var watch = await InstalledOnQuit();
        AppSwap.Acknowledge(Folder, "2.0.0");

        Assert.Equal(0, await AppSwap.RunAsync(watch).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("new", Running("new"));
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task An_old_window_saying_it_is_up_does_not_count_for_the_new_version()
    {
        if (!OperatingSystem.IsLinux()) return;
        var watch = await InstalledOnQuit(acknowledge: 1);
        AppSwap.Acknowledge(Folder, "1.0.0");

        Assert.True(File.Exists(Mark));
        Assert.Equal(1, await AppSwap.RunAsync(watch).WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal("old", Running("old"));
    }

    [Fact]
    public async Task A_next_launch_whose_backend_exits_first_puts_the_old_app_and_database_back_opens_it_and_says_so()
    {
        if (!OperatingSystem.IsLinux()) return;
        var watch = await InstalledOnQuit();
        var running = AppSwap.RunAsync(watch);
        End(Own(watch.BackendPid));

        Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal("old", Running("old"));
        Assert.Equal("original", File.ReadAllText(Database));
        Assert.Equal("migrated\n", File.ReadAllText(Database + ".failed-2.0.0"));
        Assert.Equal(WentBackKeepingLaterWrites, SaidNote());
        Assert.True(Own(watch.WindowPid).HasExited);
        Assert.True(Alive(await Started("old")));
        Assert.False(File.Exists(Mark));
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task A_next_launch_that_never_says_its_window_is_up_is_stopped_and_the_old_app_comes_back()
    {
        if (!OperatingSystem.IsLinux()) return;
        var watch = await InstalledOnQuit(acknowledge: 1);

        Assert.Equal(1, await AppSwap.RunAsync(watch).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal("old", Running("old"));
        Assert.Equal("original", File.ReadAllText(Database));
        Assert.True(Own(watch.WindowPid).HasExited);
        Assert.True(Own(watch.BackendPid).HasExited);
        Assert.Equal(WentBackKeepingLaterWrites, SaidNote());
    }

    [Fact]
    public async Task A_database_is_at_its_path_at_every_moment_of_a_going_back()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared("exit 3");
        File.WriteAllBytes(Database, new byte[64 * 1024 * 1024]);
        End(window); End(backend);

        var running = AppSwap.RunAsync(plan);
        var swapped = false;
        while (!running.IsCompleted)
        {
            swapped |= Running("new") == "new";
            if (swapped) Assert.True(File.Exists(Database), "No database at its path during the going back.");
            await Task.Yield();
        }

        Assert.Equal(1, await running);
        Assert.Equal(64 * 1024 * 1024, new FileInfo(Database).Length);
        // Nothing was written after the update, so there's nothing to point at.
        Assert.Equal("Assembly 2.0.0 didn't start, so this app went back to 1.0.0.", SaidNote());
    }

    [Fact]
    public async Task A_copy_that_cannot_be_read_leaves_the_database_where_it_is_and_says_where_the_copy_is()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo migrated > '{Database}'\nchmod 000 '{Backup}'\nexit 3");
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal("migrated\n", File.ReadAllText(Database));
        Assert.Contains("database couldn't be put back", SaidNote(), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Backup, (UnixFileMode)0b110_000_000);
    }
}
