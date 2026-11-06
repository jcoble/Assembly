using System.Net;
using System.Net.WebSockets;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class CrossMachineStartTests
{
    private sealed class StartHosts : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "assembly-start-" + Guid.NewGuid().ToString("N"));
        internal string Parent { get; } = Guid.NewGuid().ToString();
        internal string? ForeignRoot { get; private set; }
        internal RelayTestLauncher Launcher { get; } = new();
        internal TwoHosts H { get; private set; } = null!;
        internal MachineStarts Starts { get; private set; } = null!;
        private WebApplication _window = null!;
        private Uri _windowAddress = null!;
        internal string DirectFolder => Path.Combine(Root, "remote");
        internal SessionLimits Limits { get; }
        private StartHosts(int children) => Limits = new(() => 3, () => children);

        internal static async Task<StartHosts> Open(int children = 3, string title = "Parent", string? foreignRootTitle = null)
        {
            var fixture = new StartHosts(children);
            try { await fixture.Start(title, foreignRootTitle); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task Start(string title, string? foreignRootTitle)
        {
            Directory.CreateDirectory(Path.Combine(Root, "mac")); Directory.CreateDirectory(DirectFolder);
            H = new(windowCalls: true, forwardCommands: true, machineLauncher: Launcher, checkouts: Root, macLimits: Limits, remoteLimits: Limits);
            await H.Start();
            if (foreignRootTitle is not null)
            {
                ForeignRoot = Guid.NewGuid().ToString();
                var root = EngineCommands.PrepareSessionTitle(new StartSession("codex", "root-model", DirectFolder, foreignRootTitle, null, null, null));
                Assert.True(((IEngineRuntime)H.Remote.Runtime).Write(scope => H.Remote.Runtime.Commands.ApplyPreparedSessionTitle(scope,
                    new(Guid.NewGuid(), H.Remote.Runtime.Machine, new(ActorKind.Window, "owner"), ForeignRoot, 0, 0, 1000), root)).Applied);
                var foreign = H.Mac.Runtime.Commands.PrepareForeignAncestry(new(H.Remote.Runtime.Machine, ForeignRoot,
                    [new(ForeignRoot, H.Remote.Runtime.Machine, "codex", foreignRootTitle, null)]));
                ((IEngineRuntime)H.Mac.Runtime).Write(scope => H.Mac.Runtime.Commands.ApplyForeignAncestryBatch(scope, foreign));
            }
            var start = new StartSession("codex", "source-model", Path.Combine(Root, "mac"), title, ForeignRoot, null, null);
            var prepared = EngineCommands.PrepareSessionTitle(start);
            var actor = ForeignRoot is null ? new EngineActor(ActorKind.Window, "owner") : new EngineActor(ActorKind.System, "machine:" + H.Remote.Runtime.Machine);
            Assert.True(((IEngineRuntime)H.Mac.Runtime).Write(scope => H.Mac.Runtime.Commands.ApplyPreparedSessionTitle(scope,
                new(Guid.NewGuid(), H.Mac.Runtime.Machine, actor, Parent, 0, 0, 1000), prepared)).Applied);
            await H.StartMirror();
            var mirror = Assert.Single(H.Connections.CaptureMirrorOwners()).Mirror;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CaughtUp() => ready.TrySetResult();
            mirror.CaughtUp += CaughtUp;
            try
            {
                if (await mirror.CaptureLiveBindingAsync(default) is null) await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { mirror.CaughtUp -= CaughtUp; }
            var forward = new MachineForward(machine => H.Connections.ByEngineMachine(H.Mac.Fixture.Db, machine));
            Starts = new(H.Mac.Runtime, forward, H.Connections, Limits, H.Mac.Fixture.Time);
            var calls = new SocketDispatcher(H.Mac.Fixture.Db.Writer, H.Mac.Topics, H.Mac.Fixture.Time, NullLogger.Instance, new EngineSocketStream(H.Mac.Runtime));
            new EngineCalls(H.Mac.Runtime, H.Mac.Fixture.Time, H.Mac.Needs, forward, Starts).Register(calls);
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            _window = builder.Build(); _window.UseWebSockets();
            var endpoint = new SocketEndpoint("window-token", calls, H.Mac.Runtime.Machine, H.Mac.Fixture.Time, engineMachine: H.Mac.Runtime.Machine);
            _window.Map("/socket", endpoint.HandleAsync); _window.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
            await _window.StartAsync();
            var http = _window.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            _windowAddress = new UriBuilder(http) { Scheme = "ws", Path = "/socket" }.Uri;
        }

        internal async Task<ClientWebSocket> Window()
        {
            var socket = new ClientWebSocket(); await socket.ConnectAsync(_windowAddress, default);
            await Send(socket, """{"t":"hello","protocol":1,"token":"window-token"}""");
            Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
        }

        public async ValueTask DisposeAsync()
        {
            if (_window is not null) await _window.DisposeAsync();
            if (H is not null) await H.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
