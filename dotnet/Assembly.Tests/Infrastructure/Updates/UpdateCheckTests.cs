using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Topics;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

public sealed class UpdateCheckTests
{
    private static readonly RemoteBackend Same = new("a", "a", false, false, "1.0.0", "1.0.0");
    private static UpdateWanted Dev(string? adapter = "2.0.0", string? cli = "3.0.0") => new(null, backend => backend.ListDiffers, "1.0.0",
        new Dictionary<string, string?> { ["adapter-acp"] = adapter }, "sources", new Dictionary<string, string?> { ["family"] = cli });
    private static InstalledTools Tools(string adapter = "2.0.0", string? packaging = "sources", string? cli = "3.0.0") =>
        new(new Dictionary<string, string> { ["adapter-acp"] = adapter }, packaging, [new("family", cli)]);
    private static string[] Behind(IReadOnlyList<UpdateItem> items) => [.. items.Where(item => item.State == UpdateItem.Behind).Select(item => item.Kind)];

    [Fact]
    public void Everything_matching_is_not_behind() => Assert.Empty(Behind(UpdateCheck.Remote(Same, Tools(), Dev())));

    [Fact]
    public void Each_piece_is_behind_on_its_own()
    {
        Assert.Equal(["backend"], Behind(UpdateCheck.Remote(Same with { ListDiffers = true }, Tools(), Dev())));
        Assert.Equal(["adapter"], Behind(UpdateCheck.Remote(Same, Tools(adapter: "1.9.0"), Dev())));
        Assert.Equal(["adapter"], Behind(UpdateCheck.Remote(Same, Tools(packaging: "older"), Dev())));
        Assert.Equal(["cli"], Behind(UpdateCheck.Remote(Same, Tools(cli: "2.9.9"), Dev())));
        Assert.Equal(["adapter"], Behind(UpdateCheck.Local("1.0.0", Tools(adapter: "1.9.0"), Dev())));
    }

    [Fact]
    public void Development_goes_by_the_list()
    {
        var unrelated = Same with { Commit = "b", Differs = true };
        Assert.Empty(Behind(UpdateCheck.Remote(unrelated, Tools(), Dev())));
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

    private sealed class FakeSource(Func<Action<string>, CancellationToken, Task> update) : IUpdateSource
    {
        public Task<UpdateWanted> WantedAsync(CancellationToken ct) => Task.FromResult(Dev());
        public Task UpdateAsync(IReadOnlyList<string> remotes, Action<string> progress, CancellationToken ct) => update(progress, ct);
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
        var refused = await Assert.ThrowsAsync<CallFailed>(() => source.UpdateAsync(["box"], _ => { }, CancellationToken.None));
        Assert.Equal("no_release", refused.Reason);
    }

    private sealed class FakeRelease : IReleaseInstall
    {
        public List<string> Installed { get; } = [];
        public Task<string> LatestBackendAsync(CancellationToken ct) => Task.FromResult("2.0.0");
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct)
        {
            Installed.Add(machine);
            progress("Downloading.");
            return Task.CompletedTask;
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task A_release_install_runs_on_every_remote_and_a_backend_behind_the_newest_release_shows()
    {
        var release = new FakeRelease();
        var source = UpdateSources.ForHost(new HttpClient(new NoNetwork()), TimeProvider.System, release, "");
        var notes = new List<string>();
        await source.UpdateAsync(["box", "other"], notes.Add, CancellationToken.None);
        Assert.Equal(["box", "other"], release.Installed);
        Assert.Equal(["Downloading.", "Downloading."], notes);
        var wanted = await source.WantedAsync(CancellationToken.None);
        Assert.Equal(["backend"], Behind(UpdateCheck.Remote(Same, null, wanted)));
        Assert.Empty(Behind(UpdateCheck.Remote(Same with { Version = "2.0.0" }, null, wanted)));
        Assert.Empty(Behind(UpdateCheck.Remote(Same with { Version = "2.1.0" }, null, wanted)));
    }

    [Fact]
    public async Task The_button_runs_the_checkout_command_and_passes_its_progress_and_failure()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts/update-machines.ts"), """
                console.log('ASSEMBLY_REMOTE_SYNC ' + JSON.stringify({ reason: 'Waiting.' }));
                console.error('Workbox Test sync: nope');
                process.exitCode = 1;
                """);
            var notes = new List<string>();
            var source = UpdateSources.ForHost(new HttpClient(), TimeProvider.System, checkout: root);
            var failed = await Assert.ThrowsAsync<CallFailed>(() => source.UpdateAsync([], notes.Add, CancellationToken.None));
            Assert.Equal(["Waiting."], notes);
            Assert.Equal("Workbox Test sync: nope", failed.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
