using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Updates;

/// <summary>Everything before the swap, from a signed local feed into a throwaway folder. tar and the signature check are real.</summary>
public sealed class AppUpdateTests : IDisposable
{
    private const string Listing = "https://api.github.com/repos/jcoble/Assembly/releases?per_page=100";
    private const string Base = "https://github.com/jcoble/Assembly/releases/download/assembly-v2.0.0/";
    private const string Archive = "Assembly_2.0.0_aarch64.app.tar.gz";
    private readonly string _root = Directory.CreateTempSubdirectory("appupdate-").FullName;
    private readonly List<string> _steps = [];
    private readonly ReleaseSigner _signer = new();
    private string Apps => Path.Combine(_root, "Applications");
    private string App => Path.Combine(Apps, "Assembly.app");
    private string Stage => Path.Combine(Apps, ".assembly-update-2.0.0");

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(Apps)) File.SetUnixFileMode(Apps, (UnixFileMode)0b111_101_101);
        Directory.Delete(_root, recursive: true);
    }

    private byte[] Package(string version)
    {
        var bundle = Path.Combine(_root, "package/Assembly.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents/MacOS"));
        Directory.CreateDirectory(Path.Combine(bundle, "Contents/Resources/backend"));
        File.WriteAllText(Path.Combine(bundle, "Contents/Info.plist"), $"<key>CFBundleExecutable</key>\n<string>assembly-desktop</string>\n<key>CFBundleShortVersionString</key>\n<string>{version}</string>");
        File.WriteAllText(Path.Combine(bundle, "Contents/MacOS/assembly-desktop"), "window");
        File.WriteAllText(Path.Combine(bundle, "Contents/Resources/backend/packaged.json"), "{}");
        var archive = Path.Combine(_root, "package.tar.gz");
        using (var tar = Process.Start(new ProcessStartInfo("tar", ["-czf", archive, "-C", Path.Combine(_root, "package"), "Assembly.app"]) { UseShellExecute = false })!) tar.WaitForExit();
        return File.ReadAllBytes(archive);
    }

    private RemoteReleaseFeed Feed(byte[] archive, byte[]? signature = null)
    {
        var sig = Encoding.UTF8.GetString(signature ?? _signer.Sign(archive));
        var latest = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["version"] = "2.0.0",
            ["platforms"] = new Dictionary<string, object> { ["darwin-aarch64-app"] = new Dictionary<string, string> { ["signature"] = sig, ["url"] = Base + Archive } },
        });
        var listing = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            new Dictionary<string, object>
            {
                ["tag_name"] = "assembly-v2.0.0", ["draft"] = false, ["prerelease"] = false,
                ["assets"] = new object[]
                {
                    new Dictionary<string, object> { ["name"] = "latest.json", ["size"] = latest.Length, ["browser_download_url"] = Base + "latest.json" },
                    new Dictionary<string, object> { ["name"] = Archive, ["size"] = archive.Length, ["browser_download_url"] = Base + Archive },
                },
            },
        });
        return new(new HttpClient(new RemoteReleaseFeedTests.Handler(new() { [Listing] = listing, [Base + "latest.json"] = latest, [Base + Archive] = archive })), _signer.PublicKey);
    }

    private AppUpdate Update(RemoteReleaseFeed feed, Func<CancellationToken, Task>? idle = null, Func<bool>? busy = null)
    {
        Directory.CreateDirectory(Path.Combine(App, "Contents"));
        File.WriteAllText(Path.Combine(App, "Contents/old"), "old");
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        return new(new(App, home, Path.Combine(home, "assembly.db"), "1.0.0"), feed,
            async ct => { _steps.Add("idle"); if (idle is not null) await idle(ct); _steps.Add("idle done"); },
            busy ?? (() => false),
            (_, plan, _) => { Assert.True(File.Exists(plan)); _steps.Add("helper"); return Task.FromResult<Action>(() => _steps.Add("called off")); },
            () => Environment.ProcessId, () => _steps.Add("quit"));
    }

    [Fact]
    public async Task It_gets_ready_and_goes_no_further_until_the_restart_is_pressed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var notes = new List<string>();
        var update = Update(Feed(Package("2.0.0")));
        await update.StageAsync("2.0.0", notes.Add, default);

        Assert.Empty(_steps);
        Assert.Equal("2.0.0", update.Ready);
        Assert.True(File.Exists(Path.Combine(Stage, "new/Assembly.app/Contents/MacOS/assembly-desktop")));
        Assert.Equal(["Downloading Assembly 2.0.0…", "Checking Assembly 2.0.0…"], notes);

        await update.RestartAsync(notes.Add, default);

        Assert.Equal(["idle", "idle done", "helper", "quit"], _steps);
        Assert.Equal("old", File.ReadAllText(Path.Combine(App, "Contents/old")));
        Assert.True(File.Exists(Path.Combine(Stage, "new/Assembly.app/Contents/MacOS/assembly-desktop")));
        using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Stage, "plan.json")));
        Assert.Equal("Contents/MacOS/assembly-desktop", plan.RootElement.GetProperty("program").GetString());
        Assert.Equal("1.0.0", plan.RootElement.GetProperty("previous").GetString());
        Assert.Equal(["Downloading Assembly 2.0.0…", "Checking Assembly 2.0.0…", AppUpdate.WaitingWords, "Restarting into Assembly 2.0.0…"], notes);
    }

    [Fact]
    public async Task A_turn_that_starts_as_the_helper_comes_up_calls_it_off_and_goes_back_to_waiting()
    {
        if (!OperatingSystem.IsLinux()) return;
        var answers = new Queue<bool>([true, false]);
        var update = Update(Feed(Package("2.0.0")), busy: answers.Dequeue);
        await update.StageAsync("2.0.0", _ => { }, default);

        await update.RestartAsync(_ => { }, default);

        Assert.Equal(["idle", "idle done", "helper", "called off", "idle", "idle done", "helper", "quit"], _steps);
    }

    [Fact]
    public async Task A_download_larger_than_its_release_says_stops_as_it_passes_that_size()
    {
        if (!OperatingSystem.IsLinux()) return;
        var feed = Feed(Package("2.0.0"));
        var file = Path.Combine(_root, "too-big");
        var failed = await Assert.ThrowsAsync<CallFailed>(() => feed.DownloadAppAsync(new("2.0.0", Base + Archive, 5, "signature"), file, default));
        Assert.Equal("release_invalid", failed.Reason);
        Assert.True(new FileInfo(file).Length <= 5);
    }

    [Fact]
    public async Task A_bad_signature_stops_it_with_nothing_kept_and_the_app_running()
    {
        if (!OperatingSystem.IsLinux()) return;
        var archive = Package("2.0.0");
        var failed = await Assert.ThrowsAsync<CallFailed>(() => Update(Feed(archive, new ReleaseSigner().Sign(archive))).StageAsync("2.0.0", _ => { }, default));
        Assert.Equal("Release signature verification failed.", failed.Message);
        Assert.Empty(_steps);
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task A_signed_archive_of_another_version_is_not_installed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var failed = await Assert.ThrowsAsync<CallFailed>(() => Update(Feed(Package("1.5.0"))).StageAsync("2.0.0", _ => { }, default));
        Assert.Equal("The download doesn't contain Assembly 2.0.0.", failed.Message);
        Assert.Empty(_steps);
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task A_running_session_holds_the_restart_back_and_stopping_keeps_it_ready()
    {
        if (!OperatingSystem.IsLinux()) return;
        var activity = new TurnActivity();
        activity.Hold(true);
        using var stop = new CancellationTokenSource();
        var update = Update(Feed(Package("2.0.0")), activity.WhenIdleAsync);
        await update.StageAsync("2.0.0", _ => { }, default);
        var running = update.RestartAsync(_ => { }, stop.Token);
        await Task.WhenAny(running, Task.Delay(500));
        Assert.Equal(["idle"], _steps);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(["idle"], _steps);
        Assert.Equal("2.0.0", update.Ready);
    }

    [Fact]
    public async Task The_moment_the_last_turn_ends_it_carries_on()
    {
        if (!OperatingSystem.IsLinux()) return;
        var activity = new TurnActivity();
        activity.Hold(true);
        var update = Update(Feed(Package("2.0.0")), activity.WhenIdleAsync);
        await update.StageAsync("2.0.0", _ => { }, default);
        var running = update.RestartAsync(_ => { }, default);
        await Task.WhenAny(running, Task.Delay(300));
        Assert.DoesNotContain("quit", _steps);
        activity.Hold(false);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("quit", _steps[^1]);
    }

    [Fact]
    public async Task A_folder_it_cannot_write_to_says_so_with_nothing_changed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var update = Update(Feed(Package("2.0.0")));
        File.SetUnixFileMode(Apps, (UnixFileMode)0b101_101_101);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => update.StageAsync("2.0.0", _ => { }, default));
        Assert.StartsWith($"Assembly can't replace itself in {Apps}, so nothing was changed", failed.Message, StringComparison.Ordinal);
        Assert.Empty(_steps);
        Assert.Equal("old", File.ReadAllText(Path.Combine(App, "Contents/old")));
    }

    [Fact]
    public void Only_a_packaged_app_has_a_bundle_to_replace()
    {
        var backend = Path.Combine(_root, "X.app/Contents/Resources/backend");
        Directory.CreateDirectory(backend);
        Assert.Null(AppUpdate.BundleOf(backend));
        File.WriteAllText(Path.Combine(backend, "packaged.json"), "{}");
        Assert.Equal(Path.Combine(_root, "X.app"), AppUpdate.BundleOf(backend));
    }

    private sealed class FakeApp(CallFailed? fails = null, string? failedVersion = null) : IAppInstall
    {
        public readonly List<string> Installed = [];
        public string? FailedVersion => failedVersion;
        public string? Note => failedVersion is null ? null : $"Assembly {failedVersion} didn't start.";
        public string? Ready { get; private set; }
        public int Restarts;
        public void Started() { }
        public Task StageAsync(string version, Action<string> progress, CancellationToken ct)
        {
            Installed.Add(version);
            if (fails is not null) throw fails;
            Ready = version;
            return Task.CompletedTask;
        }
        public Task RestartAsync(Action<string> progress, CancellationToken ct) { Restarts++; return Task.CompletedTask; }
    }

    private sealed class FakeReleases(List<string?>? tools = null) : IReleaseInstall
    {
        public readonly List<string> Remotes = [];
        public Task<PublishedVersions> PublishedAsync(CancellationToken ct) => Task.FromResult(new PublishedVersions(null, null, null));
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct, bool keepAdapters = false) { Remotes.Add(machine); return Task.CompletedTask; }
        public Task UpdateToolsAsync(string? machine, ToolsBehind behind, Action<string> progress, CancellationToken ct) { tools?.Add(machine); return Task.CompletedTask; }
    }

    private static ReleaseUpdateSource Source(FakeReleases releases, IAppInstall app) =>
        new(new CliReleaseSource(new HttpClient(), TimeProvider.System), releases, TimeProvider.System, app);

    private static readonly MachineBehind[] AppAndRemote =
        [new(null, [new("desktop", "Desktop", "1.0.0", "2.0.0", UpdateItem.Behind)]), new("box", [new("backend", "Backend", "1.0.0", "1.0.1", UpdateItem.Behind)])];

    [Fact]
    public async Task By_itself_only_the_app_and_remotes_go_and_this_computers_tools_wait_for_a_press()
    {
        var tools = new List<string?>();
        var (app, releases) = (new FakeApp(), new FakeReleases(tools));
        MachineBehind[] here = [new(null, [new("desktop", "Desktop", "1.0.0", "2.0.0", UpdateItem.Behind), new("cli", "codex", "1.0.0", "2.0.0", UpdateItem.Behind)])];
        var source = Source(releases, app);
        await source.UpdateAsync(here, true, _ => { }, default);
        Assert.Equal(["2.0.0"], app.Installed);
        Assert.Empty(tools);
        await source.UpdateAsync(here, false, _ => { }, default);
        Assert.Equal([null], tools);
    }

    [Fact]
    public async Task One_press_gets_the_app_ready_and_updates_everything_else_too()
    {
        var (app, releases) = (new FakeApp(), new FakeReleases());
        await Source(releases, app).UpdateAsync(AppAndRemote, false, _ => { }, default);
        Assert.Equal(["2.0.0"], app.Installed);
        Assert.Equal(["box"], releases.Remotes);
    }

    [Fact]
    public async Task An_update_already_ready_is_not_fetched_again_and_only_the_restart_starts_it()
    {
        var (app, releases) = (new FakeApp(), new FakeReleases());
        var source = Source(releases, app);
        await source.UpdateAsync(AppAndRemote, true, _ => { }, default);
        await source.UpdateAsync(AppAndRemote, false, _ => { }, default);
        Assert.Equal(["2.0.0"], app.Installed);
        // A press with the app already ready still does the rest.
        Assert.Equal(["box", "box"], releases.Remotes);
        Assert.Equal(0, app.Restarts);
        Assert.Equal("2.0.0", source.AppReady);
        await source.RestartAppAsync(_ => { }, default);
        Assert.Equal(1, app.Restarts);
    }

    [Fact]
    public async Task An_app_that_could_not_update_says_why_and_the_remotes_still_go()
    {
        var (app, releases) = (new FakeApp(new("can't")), new FakeReleases());
        var failed = await Assert.ThrowsAsync<CallFailed>(() => Source(releases, app).UpdateAsync(AppAndRemote, false, _ => { }, default));
        Assert.Equal("can't", failed.Message);
        Assert.Equal(["box"], releases.Remotes);
    }

    [Fact]
    public void A_version_that_failed_to_start_is_not_tried_again_by_itself()
    {
        var source = Source(new(), new FakeApp(failedVersion: "2.0.0"));
        Assert.False(source.StartsAppUpdate("2.0.0"));
        Assert.True(source.StartsAppUpdate("2.0.1"));
        Assert.Equal("Assembly 2.0.0 didn't start.", source.AppNote);
        Assert.False(new ReleaseUpdateSource(new CliReleaseSource(new HttpClient(), TimeProvider.System), new FakeReleases(), TimeProvider.System).StartsAppUpdate("2.0.0"));
    }

    private sealed class AppBehind(bool starts) : IUpdateSource
    {
        public int Automatic, Restarts;
        public string? AppReady { get; set; }
        public Task RestartAppAsync(Action<string> progress, CancellationToken ct) { Restarts++; return Task.CompletedTask; }
        public readonly TaskCompletionSource Ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) => Task.FromResult(new UpdateWanted("99.0.0", "99.0.0", new Dictionary<string, string?>(), null, new Dictionary<string, string?>()));
        public bool StartsRemoteUpdates(string machine) => true;
        public bool StartsAppUpdate(string version) => starts;
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
        {
            if (automatic && machines.Any(machine => machine.Name is null && machine.Behind.Any(item => item.Kind == "desktop"))) Interlocked.Increment(ref Automatic);
            AppReady = "99.0.0";
            Ran.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static MachineUpdates Machines(IUpdateSource source, MachineStatus status, Assembly.Application.Common.BackgroundWork work) =>
        new(source, status, new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System), (_, _) => Task.FromResult<InstalledTools?>(null), null, work, TextWriter.Null);

    [Fact]
    public async Task An_app_behind_updates_by_itself_once_per_run()
    {
        var status = new MachineStatus(new Assembly.Infrastructure.Topics.Topics(), []);
        await using var work = new Assembly.Application.Common.BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new AppBehind(starts: true);
        var updates = Machines(source, status, work);
        await updates.CheckAsync(null, default);
        await source.Ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (status.Items[0].Updating) await Task.Delay(5, deadline.Token);
        await updates.CheckAsync(null, default);
        Assert.Equal(1, source.Automatic);
    }

    [Fact]
    public async Task A_ready_update_is_offered_and_the_app_restarts_only_when_asked()
    {
        var status = new MachineStatus(new Assembly.Infrastructure.Topics.Topics(), []);
        await using var work = new Assembly.Application.Common.BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new AppBehind(starts: true);
        var updates = Machines(source, status, work);
        await Assert.ThrowsAsync<CallFailed>(() => Task.FromResult(updates.RestartApp()));

        await updates.CheckAsync(null, default);
        await source.Ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (status.Items[0].RestartReady is null) await Task.Delay(5, deadline.Token);
        Assert.Equal("99.0.0", status.Items[0].RestartReady);
        Assert.False(status.Items[0].Quit);
        Assert.Equal(0, source.Restarts);

        Assert.True(updates.RestartApp());
        while (source.Restarts == 0) await Task.Delay(5, deadline.Token);
    }

    [Fact]
    public async Task An_app_told_not_to_start_only_shows_what_is_behind()
    {
        var status = new MachineStatus(new Assembly.Infrastructure.Topics.Topics(), []);
        await using var work = new Assembly.Application.Common.BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new AppBehind(starts: false);
        await Machines(source, status, work).CheckAsync(null, default);
        Assert.Equal(0, source.Automatic);
        Assert.Contains(status.Items[0].Updates!, item => item.Kind == "desktop" && item.State == UpdateItem.Behind);
    }
}
