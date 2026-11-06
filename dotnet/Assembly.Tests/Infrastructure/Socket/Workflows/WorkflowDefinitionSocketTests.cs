using System.Text.Json;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Projects;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Workflow_definitions_resolve_actual_project_registry_on_the_selected_owner_and_refuse_missing_selection()
    {
        using var files = new DefinitionFiles();
        await using var h = await DefinitionHosts();
        var local = DefinitionProject(h.Mac, files.Local);
        var desktopRemote = DefinitionProject(h.Mac, files.Remote, "workbox-test");
        Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM projects"));
        using var control = await Open(h.Mac);
        await Send(control, WorkflowReadCall(1, "definitions", new { project = local.Id }));
        Assert.Equal("not_found", (await Read(control)).GetProperty("code").GetString());
        using var bulk = await Open(h.Mac, true);
        await Send(bulk, WorkflowReadCall(2, "definitions", new { project = local.Id }));
        Assert.Equal("Local definition", WorkflowResult(await Read(bulk)).GetProperty("items")[0].GetProperty("name").GetString());
        await Send(bulk, WorkflowReadCall(3, "definitions", new { project = desktopRemote.Id, machine = h.Remote.Fixture.Machine }));
        Assert.Equal("Remote definition", WorkflowResult(await Read(bulk)).GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM projects"));
        await Send(bulk, WorkflowReadCall(4, "definitions", new { }));
        Assert.Equal("project_required", (await Read(bulk)).GetProperty("code").GetString());
        await Send(bulk, WorkflowReadCall(5, "definitions", new { project = desktopRemote.Id }));
        Assert.Equal("Remote definition", WorkflowResult(await Read(bulk)).GetProperty("items")[0].GetProperty("name").GetString());
        await Send(bulk, WorkflowReadCall(6, "definitions", new { project = files.Local }));
        Assert.Equal("not_found", (await Read(bulk)).GetProperty("code").GetString());
        await Send(bulk, WorkflowReadCall(7, "definitions", new { project = desktopRemote.Id, machine = "workbox-test" }));
        Assert.Equal("error", (await Read(bulk)).GetProperty("t").GetString());
        await Send(bulk, WorkflowReadCall(8, "definitions", new { project = local.Id, path = files.Local }));
        Assert.Equal("bad_args", (await Read(bulk)).GetProperty("code").GetString());
        var store = new ProjectStore(h.Mac.Fixture.Db, h.Mac.Fixture.Time);
        h.Mac.Fixture.Db.Writer.Write("test.remove-definition-project", scope => store.Remove(scope, local.Id, false));
        await Send(bulk, WorkflowReadCall(9, "definitions", new { project = local.Id }));
        Assert.Equal("not_found", (await Read(bulk)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Workflow_definitions_on_the_actual_owner_preserve_loader_containment_instead_of_reading_another_root()
    {
        using var files = new DefinitionFiles();
        await using var h = await DefinitionHosts();
        var project = DefinitionProject(h.Mac, files.Remote, "workbox-test");
        Directory.Delete(Path.Combine(files.Remote, ".assembly/workflows"), true);
        Directory.CreateSymbolicLink(Path.Combine(files.Remote, ".assembly/workflows"), Path.Combine(files.Local, ".assembly/workflows"));
        using var bulk = await Open(h.Mac, true);
        await Send(bulk, WorkflowReadCall(1, "definitions", new { project = project.Id, machine = h.Remote.Fixture.Machine }));
        var error = await Read(bulk);
        Assert.Equal("error", error.GetProperty("t").GetString());
        Assert.Contains("path", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Workflow_definitions_refuse_owner_replacement_during_the_captured_bulk_exchange()
    {
        using var files = new DefinitionFiles();
        await using var h = await DefinitionHosts();
        var project = DefinitionProject(h.Mac, files.Remote, "workbox-test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OpeningBulk = () => { entered.TrySetResult(); return release.Task; };
        using var bulk = await Open(h.Mac, true);
        try
        {
            await Send(bulk, WorkflowReadCall(1, "definitions", new { project = project.Id, machine = h.Remote.Fixture.Machine }));
            await entered.Task.WaitAsync(Wait); h.Remote.Endpoint.DropAll(); release.TrySetResult();
            var error = await Read(bulk);
            Assert.Contains(error.GetProperty("code").GetString(), new[] { "source_changed", "unreachable" });
            Assert.False(error.TryGetProperty("result", out _));
        }
        finally { release.TrySetResult(); h.Remote.OpeningBulk = null; }
    }

    [Fact]
    public async Task Workflow_definitions_cancel_joins_the_existing_remote_bulk_call_without_waiting_for_the_folder()
    {
        using var files = new DefinitionFiles();
        await using var h = await DefinitionHosts();
        var project = DefinitionProject(h.Mac, files.Remote, "workbox-test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OpeningBulk = () => { entered.TrySetResult(); return release.Task; };
        using var bulk = await Open(h.Mac, true);
        try
        {
            await Send(bulk, WorkflowReadCall(1, "definitions", new { project = project.Id, machine = h.Remote.Fixture.Machine }));
            await entered.Task.WaitAsync(Wait);
            await Send(bulk, """{"t":"cancel","id":1}""");
            var error = await Read(bulk);
            Assert.Equal("cancelled", error.GetProperty("code").GetString());
            Assert.False(error.TryGetProperty("result", out _));
        }
        finally { release.TrySetResult(); h.Remote.OpeningBulk = null; }
    }

    [Fact]
    public async Task Workflow_definitions_recheck_the_original_desktop_project_root_after_the_owner_read()
    {
        using var files = new DefinitionFiles();
        await using var h = await DefinitionHosts();
        var project = DefinitionProject(h.Mac, files.Remote, "workbox-test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OpeningBulk = () => { entered.TrySetResult(); return release.Task; };
        using var bulk = await Open(h.Mac, true);
        try
        {
            await Send(bulk, WorkflowReadCall(1, "definitions", new { project = project.Id }));
            await entered.Task.WaitAsync(Wait);
            var store = new ProjectStore(h.Mac.Fixture.Db, h.Mac.Fixture.Time);
            h.Mac.Fixture.Db.Writer.Write("test.change-definition-root", scope => store.SetRoot(scope, project.Id,
                new(files.Local, "Changed root", null, false, null, false)));
            release.TrySetResult();
            var error = await Read(bulk);
            Assert.Equal("source_changed", error.GetProperty("code").GetString());
            Assert.False(error.TryGetProperty("result", out _));
            h.Remote.OpeningBulk = null;
            await Send(bulk, WorkflowReadCall(2, "definitions", new { project = project.Id }));
            Assert.Equal("Local definition", WorkflowResult(await Read(bulk)).GetProperty("items")[0].GetProperty("name").GetString());
        }
        finally { release.TrySetResult(); h.Remote.OpeningBulk = null; }
    }

    [Fact]
    public async Task Workflow_definition_owner_root_read_is_closed_and_requires_its_actual_engine_identity()
    {
        using var files = new DefinitionFiles();
        await using var h = await DefinitionHosts();
        using var owner = await Open(h.Remote, true);
        await Send(owner, WorkflowReadCall(1, "definitions_at_root", new { machine = h.Mac.Fixture.Machine, projectRoot = files.Remote }));
        Assert.Equal("wrong_machine", (await Read(owner)).GetProperty("code").GetString());
        await Send(owner, WorkflowReadCall(2, "definitions_at_root", new { machine = h.Remote.Fixture.Machine, projectRoot = "relative" }));
        Assert.Equal("bad_args", (await Read(owner)).GetProperty("code").GetString());
        await Send(owner, WorkflowReadCall(3, "definitions_at_root", new { machine = h.Remote.Fixture.Machine, projectRoot = files.Remote, project = "forged-id" }));
        Assert.Equal("bad_args", (await Read(owner)).GetProperty("code").GetString());
        await Send(owner, WorkflowReadCall(4, "definitions_at_root", new { machine = h.Remote.Fixture.Machine, projectRoot = files.Remote }));
        Assert.Equal("Remote definition", WorkflowResult(await Read(owner)).GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM projects"));
    }

    private static async Task<TwoHosts> DefinitionHosts()
    {
        var h = new TwoHosts(windowCalls: true, forwardCommands: true);
        var localStore = new ProjectStore(h.Mac.Fixture.Db, h.Mac.Fixture.Time);
        var remoteStore = new ProjectStore(h.Remote.Fixture.Db, h.Remote.Fixture.Time);
        var local = new ProjectRoots(localStore); var remote = new ProjectRoots(remoteStore);
        h.Remote.AdditionalFeatures = calls => new Assembly.Infrastructure.Engine.Workflows.WorkflowCalls(h.Remote.Runtime,
            resolveProject: project => Assembly.Infrastructure.Engine.Workflows.WorkflowCalls.DefinitionRoot(remote, project), projects: remoteStore).Register(calls);
        try
        {
            // The actual connection owner exists before composing the window's read feature.
            await h.Remote.Start(); h.Remote.Fixture.SeedSession(); await h.StartMirror();
            h.Mac.AdditionalFeatures = calls => new Assembly.Infrastructure.Engine.Workflows.WorkflowCalls(h.Mac.Runtime, h.Mac.Forwarder,
                project => Assembly.Infrastructure.Engine.Workflows.WorkflowCalls.DefinitionRoot(local, project),
                projects: localStore, connections: h.Connections).Register(calls);
            await h.Mac.Start(); return h;
        }
        catch { await h.DisposeAsync(); throw; }
    }

    private static ProjectRecord DefinitionProject(TwoHosts.Host host, string root, string machine = "local")
    {
        var store = new ProjectStore(host.Fixture.Db, host.Fixture.Time);
        return host.Fixture.Db.Writer.Write("test.definition-project", scope => store.AddOrRestore(scope, machine, root, "Actual project", null).Project);
    }

    private sealed class DefinitionFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "assembly-definition-owner-" + Guid.NewGuid().ToString("N"));
        internal string Local => Path.Combine(root, "local");
        internal string Remote => Path.Combine(root, "remote");
        internal DefinitionFiles()
        {
            foreach (var (path, name) in new[] { (Local, "Local definition"), (Remote, "Remote definition") })
            {
                Directory.CreateDirectory(Path.Combine(path, ".assembly/workflows"));
                File.WriteAllText(Path.Combine(path, ".assembly/workflows/test.yml"), "name: " + name + "\nsteps: {}\n");
            }
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
