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
        File.WriteAllText(Backup, "original");
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
