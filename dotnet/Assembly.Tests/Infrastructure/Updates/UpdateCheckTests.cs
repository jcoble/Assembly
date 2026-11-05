using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Topics;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

public sealed class UpdateCheckTests
{
    private static readonly RemoteBackend Same = new("a", "a", false, "1.0.0", "1.0.0");
    private static UpdateWanted Dev(string? adapter = "2.0.0", string? cli = "3.0.0") => new(null, "1.0.0",
        new Dictionary<string, string?> { ["adapter-acp"] = adapter }, "sources", new Dictionary<string, string?> { ["family"] = cli });
    private static InstalledTools Tools(string adapter = "2.0.0", string? packaging = "sources", string? cli = "3.0.0") =>
        new(new Dictionary<string, string> { ["adapter-acp"] = adapter }, packaging, [new("family", cli)]);
    private static string[] Behind(IReadOnlyList<UpdateItem> items) => [.. items.Where(item => item.State == UpdateItem.Behind).Select(item => item.Kind)];

    [Fact]
    public void Everything_matching_is_not_behind() => Assert.Empty(Behind(UpdateCheck.Remote(Same, Tools(), Dev())));

    [Fact]
    public void Each_piece_is_behind_on_its_own()
    {
        Assert.Equal(["backend"], Behind(UpdateCheck.Remote(Same with { Version = "0.9.0" }, Tools(), Dev())));
        Assert.Equal(["adapter"], Behind(UpdateCheck.Remote(Same, Tools(adapter: "1.9.0"), Dev())));
        Assert.Equal(["adapter"], Behind(UpdateCheck.Remote(Same, Tools(packaging: "older"), Dev())));
        Assert.Equal(["cli"], Behind(UpdateCheck.Remote(Same, Tools(cli: "2.9.9"), Dev())));
        Assert.Equal(["adapter"], Behind(UpdateCheck.Local("1.0.0", Tools(adapter: "1.9.0"), Dev())));
    }

    [Fact]
    public void Only_the_version_number_makes_a_backend_behind()
    {
        var unrelated = Same with { Commit = "b", Differs = true };
        Assert.Empty(Behind(UpdateCheck.Remote(unrelated, Tools(), Dev())));
        Assert.Equal(["backend"], Behind(UpdateCheck.Remote(Same with { Version = null }, Tools(), Dev())));
    }

    [Fact]
    public void What_could_not_be_read_is_unknown_never_behind()
    {
        Assert.Empty(Behind(UpdateCheck.Remote(null, null, Dev(adapter: null, cli: null))));
        Assert.Empty(Behind(UpdateCheck.Remote(Same, Tools(cli: null), Dev())));
        Assert.Empty(Behind(UpdateCheck.Local("1.0.0", Tools(), Dev(adapter: null) with { Packaging = null })));
        Assert.Equal(UpdateItem.Unknown, UpdateCheck.Local("1.0.0", Tools(), Dev())[0].State);
    }

    [Fact]
    public void A_newer_install_is_not_behind() => Assert.Empty(Behind(UpdateCheck.Remote(Same, Tools(adapter: "2.1.0", cli: "3.1.0"), Dev())));

    [Fact]
    public void What_is_ahead_says_so_and_nothing_goes_backwards()
    {
        var newer = UpdateCheck.Remote(Same with { Version = "1.10.0" }, Tools(adapter: "2.1.0", cli: "3.1.0"), Dev());
        Assert.All(newer, item => Assert.Equal(UpdateItem.Newer, item.State));
        // Numbers, not text: 1.10.0 is ahead of 1.9.0, and a number that can't be compared decides nothing.
        Assert.Equal(UpdateItem.Behind, UpdateCheck.Remote(Same with { Version = "1.9.0" }, null, Dev() with { Backend = "1.10.0" })[0].State);
        Assert.Equal(UpdateItem.Unknown, UpdateCheck.Remote(Same with { Version = "1.0.0-beta" }, null, Dev())[0].State);
    }

    [Fact]
    public void Unknown_is_neither_behind_nor_newer()
    {
        var items = UpdateCheck.Remote(null, Tools(cli: null), Dev() with { Backend = null });
        Assert.DoesNotContain(items, item => item.Kind is "backend" or "cli" && item.State is UpdateItem.Behind or UpdateItem.Newer);
        Assert.Equal(UpdateItem.Unknown, UpdateCheck.Remote(Same, null, Dev() with { Backend = null })[0].State);
    }

    [Fact]
    public void One_adapter_ahead_holds_the_bundle_back_since_they_install_together()
    {
        var wanted = Dev() with { Adapters = new Dictionary<string, string?> { ["first-acp"] = "2.0.0", ["second-acp"] = "2.0.0" } };
        InstalledTools Have(string first, string second, string packaging = "sources") => new(new Dictionary<string, string> { ["first-acp"] = first, ["second-acp"] = second }, packaging, []);
        Assert.Equal(["adapter", "adapter"], Behind(UpdateCheck.Remote(Same, Have("1.0.0", "1.0.0"), wanted)));
        var held = UpdateCheck.Remote(Same, Have("2.1.0", "1.0.0"), wanted);
        Assert.Empty(Behind(held));
        Assert.Equal(UpdateItem.Newer, held.Single(item => item.Name == "first-acp").State);
        Assert.Equal(UpdateItem.Unknown, held.Single(item => item.Name == "second-acp").State);
        // Different packaging doesn't bring an older adapter either.
        Assert.Empty(Behind(UpdateCheck.Remote(Same, Have("2.1.0", "2.0.0", "older"), wanted)));
    }

    [Fact]
    public void A_missing_bundle_is_behind() =>
        Assert.Equal(["adapter"], Behind(UpdateCheck.Remote(Same, new(new Dictionary<string, string>(), null, []), Dev())));

    [Fact]
    public void An_adapter_manifest_is_found_from_any_entry_in_its_bundle()
    {
        var bundle = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(bundle, "node_modules/x/dist"));
            File.WriteAllText(Path.Combine(bundle, InstalledCalls.Manifest), "{}");
            Assert.Equal(Path.Combine(bundle, InstalledCalls.Manifest), InstalledCalls.ManifestOf([Path.Combine(bundle, "node_modules/x/dist/index.js")]));
            Assert.Null(InstalledCalls.ManifestOf(["relative/index.js"]));
            Assert.Null(InstalledCalls.ManifestOf([]));
        }
        finally { Directory.Delete(bundle, true); }
    }

    [Fact]
    public void A_result_and_a_running_update_reach_the_machine_through_its_state_push()
    {
        var topics = new Topics();
        var status = new MachineStatus(topics, ["box"]);
        var pushed = new List<string?>();
        using var watching = topics.Subscribe("machines", null, _ => { }, (key, _) => pushed.Add(key));
        IReadOnlyList<UpdateItem> items = [new("backend", "Backend", "1", "2", UpdateItem.Behind)];
        status.Updates("box", items);
        status.Updates("box", [.. items]);
        status.Updates(null, items);
        status.Updating("box", true, "Waiting.");
        Assert.Equal(["box", "local", "box"], pushed);
        Assert.Equal("Waiting.", status.Items[1].UpdateNote);
        Assert.True(status.Items[1].Updating);
        Assert.Equal(items, status.Items[0].Updates);
    }

    private sealed class FakeSource(Func<Action<string>, CancellationToken, Task> update, bool starts = false) : IUpdateSource
    {
        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) => Task.FromResult(Dev());
        public bool StartsRemoteUpdates(string machine) => starts;
        public int Automatic;
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => update(progress, ct);
        public Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
        {
            if (automatic) Interlocked.Increment(ref Automatic);
            return update(progress, ct);
        }
    }

    private static MachineUpdates Updates(IUpdateSource source, MachineStatus status, BackgroundWork work, Action? reload = null) =>
        new(source, status, new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System),
            (_, _) => Task.FromResult<InstalledTools?>(Tools()), reload, work, TextWriter.Null);

    private static async Task Until(Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(5, deadline.Token);
    }

    [Fact]
    public async Task One_update_runs_at_a_time_then_rereads_and_rechecks_every_machine()
    {
        var status = new MachineStatus(new Topics(), ["box"]);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var finish = new TaskCompletionSource();
        var runs = 0; var reloads = 0;
        var updates = Updates(new FakeSource(async (progress, _) => { runs++; progress("Waiting."); await finish.Task; }), status, work, () => reloads++);
        Assert.True(updates.Start());
        await Until(() => status.Items[1].UpdateNote == "Waiting.");
        Assert.False(updates.Start());
        Assert.True(status.Items[0].Updating);
        finish.SetResult();
        await Until(() => !status.Items[1].Updating && status.Items.All(item => item.Updates is not null));
        Assert.Equal(1, runs);
        Assert.Equal(1, reloads);
        Assert.Null(status.Items[1].UpdateNote);
        Assert.Contains(status.Items[1].Updates!, item => item.Kind == "backend");
        Assert.Contains(status.Items[0].Updates!, item => item.Kind == "desktop");
    }

    [Fact]
    public async Task A_failed_update_says_why_and_a_new_one_can_start()
    {
        var status = new MachineStatus(new Topics(), ["box"]);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var updates = Updates(new FakeSource((_, _) => throw new CallFailed("compile error")), status, work);
        Assert.True(updates.Start());
        await Until(() => status.Items[1].UpdateNote is not null && !status.Items[1].Updating);
        Assert.Equal("Update failed: compile error", status.Items[1].UpdateNote);
        Assert.True(updates.Start());
    }

    [Fact]
    public async Task A_delivered_app_refuses_to_update_until_a_release_exists()
    {
        var source = UpdateSources.ForHost(new HttpClient(), TimeProvider.System, checkout: "");
        Assert.IsType<ReleaseUpdateSource>(source);
        var refused = await Assert.ThrowsAsync<CallFailed>(() => source.UpdateAsync([new("box", [Item("backend")])], false, _ => { }, CancellationToken.None));
        Assert.Equal("no_release", refused.Reason);
    }

    private static UpdateItem Item(string kind, string name = "x", string wanted = "9.0.0") => new(kind, name, "1.0.0", wanted, UpdateItem.Behind);

    private sealed class FakeRelease : IReleaseInstall
    {
        public List<string> Installed { get; } = [];
        public List<(string? Machine, ToolsBehind Tools)> Tools { get; } = [];
        public PublishedVersions Published { get; set; } = new(null, null, null);
        public int Reads { get; private set; }
        public string? FailOn { get; init; }
        public List<string> KeptAdapters { get; } = [];
        public Task<PublishedVersions> PublishedAsync(CancellationToken ct) { Reads++; return Task.FromResult(Published); }
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct, bool keepAdapters = false)
        {
            if (machine == FailOn) throw new CallFailed($"{machine} is unreachable.");
            Installed.Add(machine);
            if (keepAdapters) KeptAdapters.Add(machine);
            progress("Downloading.");
            return Task.CompletedTask;
        }
        public Task UpdateToolsAsync(string? machine, ToolsBehind tools, Action<string> progress, CancellationToken ct)
        {
            Tools.Add((machine, tools));
            return Task.CompletedTask;
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task A_release_install_runs_on_each_remote_behind_and_a_delivered_app_compares_the_same_number()
    {
        var release = new FakeRelease();
        var source = UpdateSources.ForHost(new HttpClient(new NoNetwork()), TimeProvider.System, release, "");
        var notes = new List<string>();
        await source.UpdateAsync([new("box", [Item("backend")]), new("other", [Item("backend")])], false, notes.Add, CancellationToken.None);
        Assert.Equal(["box", "other"], release.Installed);
        Assert.Equal(["box: Downloading.", "other: Downloading."], notes);
        var wanted = await source.WantedAsync(CancellationToken.None);
        Assert.Equal(BackendBuild.Version, wanted.Backend);
        Assert.Equal(["backend"], Behind(UpdateCheck.Remote(Same with { Version = "0.0.0" }, null, wanted)));
        Assert.Empty(Behind(UpdateCheck.Remote(Same with { Version = BackendBuild.Version }, null, wanted)));
    }

    [Fact]
    public async Task The_button_runs_the_checkout_command_and_passes_its_progress_and_failure()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts/update-machines.ts"), """
                import { writeFileSync } from 'node:fs';
                writeFileSync(new URL('args', import.meta.url), process.argv.slice(2).join(' '));
                console.log('ASSEMBLY_REMOTE_SYNC ' + JSON.stringify({ reason: 'Waiting.' }));
                console.error('Workbox Test sync: nope');
                process.exitCode = 1;
                """);
            var notes = new List<string>();
            var source = UpdateSources.ForHost(new HttpClient(), TimeProvider.System, checkout: root, home: root);
            var failed = await Assert.ThrowsAsync<CallFailed>(() => source.UpdateAsync([new(null, [Item("adapter", "codex-acp")])], false, notes.Add, CancellationToken.None));
            Assert.Equal(["Waiting."], notes);
            Assert.Equal($"--this-machine --home {root}", File.ReadAllText(Path.Combine(root, "scripts/args")));
            Assert.Equal("Workbox Test sync: nope", failed.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ReleaseUpdateSource Delivered(FakeRelease release, TimeProvider? time = null) =>
        new(new CliReleaseSource(new HttpClient(new NoNetwork()), TimeProvider.System), release, time ?? TimeProvider.System);

    [Fact]
    public async Task The_button_updates_only_what_is_behind_and_a_current_backend_stays()
    {
        var release = new FakeRelease();
        await Delivered(release).UpdateAsync(
        [
            new(null, [Item("adapter", "codex-acp"), Item("cli", "codex", "4.0.0")]),
            new("tools-only", [Item("adapter", "codex-acp")]),
            new("cli-only", [Item("cli", "claude", "5.0.0")]),
            new("backend", [Item("backend"), Item("adapter", "codex-acp"), Item("cli", "claude", "5.0.0")]),
            new("current", []),
        ], false, _ => { }, default);
        Assert.Equal(["backend"], release.Installed);
        Assert.Equal([null, "tools-only", "cli-only", "backend"], release.Tools.Select(tool => tool.Machine));
        Assert.True(release.Tools[0].Tools.Adapters);
        Assert.Equal([new CliBehind("codex", "4.0.0")], release.Tools[0].Tools.Clis);
        Assert.Equal([], release.Tools[1].Tools.Clis);
        Assert.False(release.Tools[2].Tools.Adapters);
        // The backend's install brought its adapters, so only its CLI is left.
        Assert.False(release.Tools[3].Tools.Adapters);
        Assert.Equal([new CliBehind("claude", "5.0.0")], release.Tools[3].Tools.Clis);
    }

    [Fact]
    public async Task One_machine_failing_does_not_hold_up_the_rest_and_the_failure_is_said()
    {
        var release = new FakeRelease { FailOn = "down" };
        var failed = await Assert.ThrowsAsync<CallFailed>(() => Delivered(release).UpdateAsync([new("down", [Item("backend")]), new("up", [Item("backend")])], false, _ => { }, default));
        Assert.Equal(["up"], release.Installed);
        Assert.Contains("down is unreachable.", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_button_says_it_cannot_install_the_app_and_never_claims_it_did()
    {
        var release = new FakeRelease();
        MachineBehind[] behind = [new(null, [Item("desktop", "Desktop", "0.3.0")])];
        var failed = await Assert.ThrowsAsync<CallFailed>(() => Delivered(release).UpdateAsync(behind, false, _ => { }, default));
        Assert.Contains("Assembly 0.3.0", failed.Message, StringComparison.Ordinal);
        // By itself it stays quiet about that, since nobody asked.
        await Delivered(release).UpdateAsync(behind, true, _ => { }, default);
    }

    [Fact]
    public async Task The_check_takes_the_app_and_adapters_from_the_release_and_a_newer_remote_is_not_behind()
    {
        var release = new FakeRelease { Published = new("9.9.9", new Dictionary<string, string> { ["codex-acp"] = "7.0.0" }, "packed") };
        var wanted = await Delivered(release).WantedAsync(default);
        Assert.Equal("9.9.9", wanted.App);
        Assert.Equal("7.0.0", wanted.Adapters["codex-acp"]);
        Assert.Null(wanted.Adapters["claude-agent-acp"]);
        Assert.Equal("packed", wanted.Packaging);
        Assert.Equal(["desktop"], Behind(UpdateCheck.Local("1.0.0", null, wanted)));
        Assert.Equal(["adapter"], Behind(UpdateCheck.Remote(null, new(new Dictionary<string, string> { ["codex-acp"] = "6.0.0", ["claude-agent-acp"] = "6.0.0" }, "packed", []), wanted)));

        var mine = wanted with { Backend = "1.0.0" };
        Assert.Equal(["backend"], Behind(UpdateCheck.Remote(Same with { Version = "0.9.0" }, null, mine)));
        Assert.Equal(["backend"], Behind(UpdateCheck.Remote(Same with { Version = null }, null, mine)));
        Assert.Empty(Behind(UpdateCheck.Remote(Same with { Version = "1.1.0" }, null, mine)));
        // The one rule, the same for a checkout: the side with the lower number updates, never the other way.
        Assert.Empty(Behind(UpdateCheck.Remote(Same with { Version = "1.1.0" }, Tools(), Dev())));
    }

    [Fact]
    public async Task Nothing_published_or_readable_is_unknown_and_never_behind()
    {
        var wanted = await Delivered(new FakeRelease()).WantedAsync(default);
        Assert.Null(wanted.App);
        Assert.All(wanted.Adapters.Values, Assert.Null);
        var installed = new InstalledTools(new Dictionary<string, string> { ["codex-acp"] = "1.0.0", ["claude-agent-acp"] = "1.0.0" }, "any", []);
        Assert.Empty(Behind(UpdateCheck.Local("1.0.0", installed, wanted)));
    }

    [Fact]
    public async Task The_published_versions_are_read_once_within_a_while()
    {
        var release = new FakeRelease { Published = new("9.9.9", null, null) };
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = Delivered(release, clock);
        await source.WantedAsync(default);
        await source.WantedAsync(default);
        Assert.Equal(1, release.Reads);
        clock.Advance(TimeSpan.FromMinutes(11));
        await source.WantedAsync(default);
        Assert.Equal(2, release.Reads);
    }

    private sealed class Welcome(string version)
    {
        internal readonly MachineStatus Status = new(new Topics(), ["box"]);
        internal void Say() => Status.Backend("box", new("a", "a", false, version, "1.0.0"));
    }

    [Fact]
    public async Task A_remote_on_an_older_version_starts_the_update_at_its_hello_with_no_button()
    {
        var welcome = new Welcome("0.0.1");
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new FakeSource((_, _) => Task.CompletedTask, starts: true);
        var updates = Updates(source, welcome.Status, work);
        updates.Begin();
        welcome.Say();
        await Until(() => source.Automatic == 1);
    }

    [Fact]
    public async Task A_remote_on_a_higher_version_starts_nothing_and_says_it_is_newer()
    {
        var welcome = new Welcome("99.0.0");
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new FakeSource((_, _) => Task.CompletedTask, starts: true);
        var updates = Updates(source, welcome.Status, work);
        welcome.Say();
        await updates.CheckAsync("box", default);
        Assert.Equal(UpdateItem.Newer, welcome.Status.Items[1].Updates![0].State);
        Assert.Equal(0, source.Automatic);
        Assert.False(welcome.Status.Items[1].Updating);
    }

    [Fact]
    public async Task A_backend_install_leaves_adapters_that_are_ahead_of_the_release()
    {
        var release = new FakeRelease();
        await Delivered(release).UpdateAsync([new("box", [Item("backend")], AdaptersNewer: true), new("other", [Item("backend")])], false, _ => { }, default);
        Assert.Equal(["box"], release.KeptAdapters);
        Assert.Equal(["box", "other"], release.Installed);
    }

    [Fact]
    public async Task A_failing_update_by_itself_is_not_tried_over_and_over_and_the_button_still_works()
    {
        var welcome = new Welcome("0.0.1");
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var attempts = 0;
        var source = new FakeSource((_, _) => { attempts++; throw new CallFailed("no release"); }, starts: true);
        var updates = Updates(source, welcome.Status, work);
        await updates.CheckAsync("box", default);
        Assert.Equal(UpdateItem.Unknown, welcome.Status.Items[1].Updates![0].State);
        welcome.Status.Welcomed = null;
        welcome.Say();
        await updates.CheckAsync("box", default);
        await Until(() => welcome.Status.Items[1].UpdateNote == "Update failed: no release" && !welcome.Status.Items[1].Updating);
        // The failed update's own recheck and every later hello come back behind, and start nothing more.
        await updates.CheckAsync("box", default);
        await updates.CheckAsync("box", default);
        Assert.Equal(1, attempts);
        Assert.True(updates.Start());
        await Until(() => attempts == 2);
    }

    [Fact]
    public async Task A_checkout_leaves_the_launch_update_to_app_dev()
    {
        var welcome = new Welcome("0.0.1");
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new FakeSource((_, _) => Task.CompletedTask);
        var updates = Updates(source, welcome.Status, work);
        welcome.Status.Welcomed = null;
        welcome.Say();
        await updates.CheckAsync("box", default);
        Assert.Equal(UpdateItem.Behind, welcome.Status.Items[1].Updates![0].State);
        Assert.Equal(0, source.Automatic);
        Assert.False(welcome.Status.Items[1].Updating);
    }

    [Fact]
    public async Task A_dev_button_updates_this_apps_own_home_and_only_the_machines_in_its_settings()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts/update-machines.ts"), """
                import { appendFileSync } from 'node:fs';
                appendFileSync(new URL('args', import.meta.url), process.argv.slice(2).join(' ') + '\n');
                """);
            var release = new FakeRelease();
            var source = UpdateSources.ForHost(new HttpClient(), TimeProvider.System, release, root, Path.Combine(root, "app-home"));
            await source.UpdateAsync([new(null, [Item("cli", "codex")]), new("box", [Item("backend")]), new("current", [])], false, _ => { }, default);
            Assert.Equal($"--this-machine --home {Path.Combine(root, "app-home")}\n", File.ReadAllText(Path.Combine(root, "scripts/args")));
            Assert.Equal(["box"], release.Installed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task An_install_takes_the_one_update_place_says_how_it_goes_and_a_failure_is_said()
    {
        var status = new MachineStatus(new Topics(), ["box"]);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var finish = new TaskCompletionSource();
        var updates = Updates(new FakeSource(async (progress, _) => { progress("Sending."); await finish.Task; throw new CallFailed("Port 9797 on box is in use by x."); }), status, work);
        Assert.Throws<CallFailed>(() => updates.Install("nowhere"));
        Assert.True(updates.Install("box"));
        await Until(() => status.Items[1].UpdateNote == "Sending.");
        Assert.False(updates.Install("box"));
        Assert.False(updates.Start());
        finish.SetResult();
        await Until(() => !status.Items[1].Updating);
        Assert.Equal("Install failed: Port 9797 on box is in use by x.", status.Items[1].UpdateNote);
    }

    [Fact]
    public async Task Check_for_updates_answers_on_the_card_gives_way_to_nothing_old_and_installs_nothing()
    {
        var status = new MachineStatus(new Topics(), ["box"]);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var runs = 0;
        var updates = Updates(new FakeSource((_, _) => { runs++; return Task.CompletedTask; }), status, work);
        status.Updating("box", false, "Update failed: an old failure.");

        await updates.CheckNowAsync("box", default);

        Assert.NotNull(status.Items[1].Updates);
        Assert.Null(status.Items[1].UpdateNote);
        Assert.Equal(0, runs);
        await Assert.ThrowsAsync<CallFailed>(() => updates.CheckNowAsync("nowhere", default));
    }

    [Fact]
    public async Task Check_for_updates_reads_the_release_again_where_other_checks_use_what_was_kept()
    {
        var release = new FakeRelease { Published = new("1.0.0", null, null) };
        var source = Delivered(release);
        await source.WantedAsync(default);
        await source.WantedAsync(default);
        Assert.Equal(1, release.Reads);
        await source.WantedAsync(default, fresh: true);
        Assert.Equal(2, release.Reads);
    }

    [Fact]
    public async Task No_published_release_says_so_when_a_delivered_app_is_asked_to_install()
    {
        var failed = await Assert.ThrowsAsync<CallFailed>(() => new ReleaseUpdateSource(new CliReleaseSource(new HttpClient(new NoNetwork()), TimeProvider.System), null, TimeProvider.System).InstallAsync("box", _ => { }, default));
        Assert.Equal("No signed release is published yet, so there's nothing to install.", failed.Message);
    }
}
