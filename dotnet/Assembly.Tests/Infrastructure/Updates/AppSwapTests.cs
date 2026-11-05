using System.Diagnostics;
using System.Text.Json;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

/// <summary>The helper on real processes and a real rename, with shell scripts as the two versions of the app.</summary>
public sealed class AppSwapTests : IDisposable
{
    private const string Silent = "sleep 300 & echo $! > \"$HOME_DIR/backend.lock.pid\"; exec sleep 300";
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
            if (int.TryParse(File.ReadAllText(file).Trim(), out var pid)) StopIfRunning(pid);
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
        Assert.Equal("Assembly 2.0.0 didn't start, so this app went back to 1.0.0.", SaidNote());
        Assert.True(Alive(await Started("old")));
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task Going_back_loses_nothing_written_right_up_to_the_quit_and_keeps_one_of_each_leftover()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo migrated > '{Database}'\nrm -f '{Database}-wal'\nexit 3");
        File.WriteAllText(Path.Combine(Folder, "assembly-before-1.0.0.db"), "an older copy");
        File.WriteAllText(Database + ".failed-1.5.0", "an older failure");
        // A turn's last rows, in the database and still in its write-ahead log, after the plan was written.
        File.WriteAllText(Database, "written late");
        File.WriteAllText(Database + "-wal", "written later");
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("written late", File.ReadAllText(Database));
        Assert.Equal("written later", File.ReadAllText(Database + "-wal"));
        Assert.Equal([Path.GetFileName(Backup), Path.GetFileName(Backup) + "-wal"], Directory.GetFiles(Folder, "assembly-before-*").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Database + ".failed-1.5.0"));
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
        Assert.False(Directory.Exists(Stage));
        Assert.False(File.Exists(Path.Combine(Folder, "failed.json")));
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
        Assert.False(Alive(int.Parse(File.ReadAllText(Path.Combine(Home, "backend.lock.pid")).Trim())));
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
}
