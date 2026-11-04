using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Desktop.Serve;
using Assembly.Infrastructure.Engine;
using Assembly.Engine.Hosting;
using Assembly.Infrastructure.Remote;
using Assembly.Remote.Serve;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class SessionCreateCompositionTests
{
    [Fact]
    public async Task Actual_desktop_and_serve_hosts_create_and_reconcile_on_the_captured_remote_owner()
    {
        const string token = "session-composition-token";
        const string route = "workbox-test";
        var temporary = Directory.CreateTempSubdirectory("assembly-create-composition-").FullName;
        using var stop = new CancellationTokenSource();
        Task<int>? serving = null;
        try
        {
            var remoteHome = Directory.CreateDirectory(Path.Combine(temporary, "remote")).FullName;
            var desktopHome = Directory.CreateDirectory(Path.Combine(temporary, "desktop")).FullName;
            var root = Directory.CreateDirectory(Path.Combine(temporary, "project")).FullName;
            var tokenFile = Path.Combine(remoteHome, "token");
            File.WriteAllText(tokenFile, $"ASSEMBLY_TOKEN={token}\n");
            var remoteLauncher = new FakeLauncher();
            var desktopLauncher = new FakeLauncher();
            var ready = new TaskCompletionSource<(Uri Address, EngineRuntime Runtime)>(TaskCreationOptions.RunContinuationsAsynchronously);
            serving = ServeRun.RunAsync(new(0, remoteHome, tokenFile, Path.Combine(remoteHome, "agents.json")),
                remoteLauncher, null, (address, runtime) => ready.TrySetResult((address, runtime)), stop.Token);
            if (await Task.WhenAny(ready.Task, serving).WaitAsync(TimeSpan.FromSeconds(15)) == serving) await serving;
            var remote = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            EngineRuntime? desktopEngine = null;
            await using var desktop = await DesktopBackend.StartAsync(desktopHome, token, 0, desktopLauncher, TextWriter.Null,
                remote: new RemoteConfig(route, remote.Address, token), engineStarted: runtime => desktopEngine = runtime);
            Assert.NotNull(desktopEngine);
            Assert.NotEqual(desktopEngine.Machine, remote.Runtime.Machine);
            using var socket = await ApiTests.OpenOwnerSocketAsync(desktop.Address, token);
            var source = await Call(socket, 1, "sessions", "resolve_source", new { routeMachine = route });
            Assert.Equal(remote.Runtime.Machine, source.GetProperty("machine").GetString());
            Assert.Equal(route, source.GetProperty("routeMachine").GetString());
            Assert.True(source.GetProperty("generation").GetInt64() > 0);

            var created = await Call(socket, 2, "sessions", "create", new
            {
                agent = "claude",
                clientId = "composition-rootless",
                machine = route,
                title = "Rootless production composition",
                expectedSource = source
            });
            var session = AssertOwned(created, remote.Runtime.Machine);
            Assert.Null(created.GetProperty("projectId").GetString());
            var reconciled = await Call(socket, 3, "sessions", "byClient", new
            {
                clientId = "composition-rootless",
                machine = route,
                expectedSource = source
            });
            Assert.Equal(session, AssertOwned(reconciled, remote.Runtime.Machine));
            Assert.Equal(created.GetProperty("facts").GetRawText(), reconciled.GetProperty("facts").GetRawText());

            // Real desktop registry inspection forwards to this actual remote owner; no registry is copied there.
            var added = await Call(socket, 4, "projects", "add", new { machine = route, path = root });
            var project = added.GetProperty("project");
            var projectId = project.GetProperty("id").GetString()!;
            Assert.Equal(route, project.GetProperty("machine").GetString());
            Assert.Equal(root, project.GetProperty("rootPath").GetString());
            var associated = await Call(socket, 5, "sessions", "create", new
            {
                agent = "claude",
                clientId = "composition-project",
                machine = route,
                projectId,
                title = "Project production composition",
                expectedSource = source
            });
            var associatedSession = AssertOwned(associated, remote.Runtime.Machine);
            Assert.NotEqual(session, associatedSession);
            Assert.Equal(projectId, associated.GetProperty("projectId").GetString());
            Assert.Equal(root, associated.GetProperty("acceptedRoot").GetString());
            Assert.Equal(root, associated.GetProperty("facts").GetProperty("folder").GetString());
            Assert.False(associated.GetProperty("facts").GetProperty("managedCheckout").GetBoolean());
            var associatedReplay = await Call(socket, 6, "sessions", "byClient", new
            {
                clientId = "composition-project",
                machine = route,
                expectedSource = source
            });
            Assert.Equal(associatedSession, AssertOwned(associatedReplay, remote.Runtime.Machine));
            Assert.Equal(projectId, associatedReplay.GetProperty("projectId").GetString());
            Assert.Equal(root, associatedReplay.GetProperty("acceptedRoot").GetString());

            // The same IDs are accepted on the owner itself, not just present in a desktop display copy.
            using var owner = await ApiTests.OpenOwnerSocketAsync(remote.Address, token);
            var ownerReplay = await Call(owner, 1, "sessions", "byClient", new { clientId = "composition-project" });
            Assert.Equal(associatedSession, AssertOwned(ownerReplay, remote.Runtime.Machine));
            Assert.Equal(projectId, ownerReplay.GetProperty("projectId").GetString());
            Assert.Equal(root, ownerReplay.GetProperty("facts").GetProperty("folder").GetString());
            var ancestry = ((IEngineRuntime)desktopEngine).Read(scope =>
                desktopEngine.Commands.ReadKnownAncestry(scope, associatedSession), CancellationToken.None);
            Assert.NotNull(ancestry);
            var reference = Assert.Single(ancestry);
            Assert.Equal(associatedSession, reference.SessionId);
            Assert.Equal(remote.Runtime.Machine, reference.Machine);
            Assert.Equal("claude", reference.Provider);
            Assert.Null(reference.ParentId);
            Assert.Equal(0, remoteLauncher.Launches);
            Assert.Equal(0, desktopLauncher.Launches);
        }
        finally
        {
            stop.Cancel();
            try
            {
                if (serving is not null)
                    try { await serving.WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (OperationCanceledException) { }
            }
            finally { Directory.Delete(temporary, recursive: true); }
        }
    }

    private static async Task<JsonElement> Call(WebSocket socket, long id, string feature, string method, object args)
    {
        var frame = await ApiTests.SocketRequestAsync(socket, id, feature, method, JsonSerializer.Serialize(args));
        var type = frame.GetProperty("t").GetString();
        var code = frame.TryGetProperty("code", out var c) ? c.GetString() : null;
        var message = frame.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.True(type == "result", $"{feature}/{method}: expected result; t={type}, code={code}, message={message}");
        return ApiTests.Result(frame);
    }

    private static string AssertOwned(JsonElement created, string machine)
    {
        var session = created.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(session));
        Assert.Equal(machine, created.GetProperty("facts").GetProperty("machine").GetString());
        Assert.Equal("claude", created.GetProperty("facts").GetProperty("agent").GetString());
        return session!;
    }
}
