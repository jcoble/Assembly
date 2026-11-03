using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Agents;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Draft.Terminal;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class SignInTerminalRelayTests
{
    [Fact]
    public async Task Registered_challenge_qualifies_the_real_program_and_fixed_read_needs_no_terminal_start_or_list()
    {
        await using var f = await Fixture.Start();
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var result = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1, "providers", "challenge",
            JsonSerializer.Serialize(new { operationId = "operation", machine = f.Hosts.Remote.Runtime.Machine })));
        Assert.Equal("operation", result.GetProperty("operationId").GetString());
        Assert.Equal(f.Challenge.Generation, result.GetProperty("generation").GetInt64());
        var id = result.GetProperty("terminalId").GetString()!;
        Assert.Equal(f.Hosts.ConnectedMachine.Terminals.Qualify(f.Program.Id), id);
        Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
        var output = new TaskCompletionSource<OutputRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewer = f.Hosts.Mac.Topics.Subscribe("terminal", id, _ => { }, (_, json) =>
        {
            var row = JsonSerializer.Deserialize(json, TerminalJson.Default.OutputRow)!;
            if (row.Data is { Length: > 0 }) output.TrySetResult(row);
        });
        await f.Routed.ReadAsync(id, 0, default);
        f.RemotePty.Started[0].Emit("inert-program-output"u8.ToArray());
        Assert.Equal("inert-program-output"u8.ToArray(), (await output.Task.WaitAsync(TimeSpan.FromSeconds(10))).Data);
        Assert.Equal(0, f.OwnerCalls.Starts); Assert.Equal(0, f.OwnerCalls.Lists);
        Assert.Single(f.RemotePty.Started); Assert.Empty(f.LocalPty.Started);
        Assert.Equal(0, f.Hosts.Mac.Fixture.Count("engine_sign_in"));
        Assert.Equal(0, f.Hosts.Remote.Fixture.Count("engine_sign_in"));
    }

    [Fact]
    public async Task Local_missing_or_explicit_local_machine_keeps_the_local_id_and_remote_host_refuses_a_foreign_target()
    {
        await using var f = await Fixture.Start();
        var local = await f.Local.StartProgramAsync(new ProgramStart("/inert/program", [], new Dictionary<string, string?>(),
            f.Folder, "signin:local-inert"), default);
        f.LocalBoard.Post("local", null, null, null, false, local.Id, null);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        foreach (var machine in new string?[] { null, f.Hosts.Mac.Runtime.Machine })
        {
            var answer = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1, "providers", "challenge",
                JsonSerializer.Serialize(new { operationId = "local", machine })));
            Assert.Equal(local.Id, answer.GetProperty("terminalId").GetString());
        }
        using var owner = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Remote.Address, "isolated-token");
        var refused = await ApiTests.SocketRequestAsync(owner, 1, "providers", "challenge",
            JsonSerializer.Serialize(new { operationId = "operation", machine = f.Hosts.Mac.Runtime.Machine }));
        Assert.Equal("wrong_machine", refused.GetProperty("code").GetString());
        Assert.Equal(f.Challenge, f.Board.Current("operation"));
        Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
    }

    [Fact]
    public async Task Reply_guard_and_strict_registration_refuse_the_original_binding_after_same_engine_profile_replacement()
    {
        await using var f = await Fixture.Start();
        var engine = f.Hosts.Remote.Runtime.Machine;
        var original = await f.Hosts.ConnectedMachine.CaptureOwnerBindingAsync(engine, default);
        var context = new CallContext(default, "inert-owner", Peer: new OwnerPeer());
        var reply = await f.Relay.ReadChallengeAsync(engine, "operation", context);
        Assert.NotNull(reply);
        var guard = Assert.IsAssignableFrom<IExpectedSourceGuard>(context.SourceGuard);
        try
        {
            guard.EnsureCurrent();
            await f.Hosts.DisconnectMirror();
            await f.Hosts.ReconnectSaved();
            Assert.True(original.IsInvalidated);
            Assert.Equal(engine, f.Hosts.Remote.Runtime.Machine);
            var error = Assert.Throws<CallFailed>(() => guard.EnsureCurrent());
            Assert.Equal("source_changed", error.Reason);
            var connections = f.Hosts.Remote.TerminalConnections;
            var registration = await Assert.ThrowsAsync<CallFailed>(() => f.Hosts.ConnectedMachine.Terminals.RegisterAsync(
                engine, f.Program.Id, original, default));
            Assert.Equal("source_changed", registration.Reason);
            Assert.Equal(connections, f.Hosts.Remote.TerminalConnections);
        }
        finally { guard.Dispose(); }
    }

    [Fact]
    public async Task Registered_challenge_cannot_qualify_a_late_reply_after_its_captured_profile_is_disconnected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(async (method, _) =>
        {
            if (method != "challenge") return;
            entered.TrySetResult(); await release.Task;
        });
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var call = ApiTests.SocketRequestAsync(socket, 1, "providers", "challenge",
            JsonSerializer.Serialize(new { operationId = "operation", machine = f.Hosts.Remote.Runtime.Machine }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Hosts.DisconnectMirror();
            release.TrySetResult();
            var frame = await call.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("error", frame.GetProperty("t").GetString());
            Assert.False(frame.TryGetProperty("result", out _));
            Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
            Assert.Single(f.RemotePty.Started); Assert.Empty(f.LocalPty.Started);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("another-operation", 1)]
    [InlineData("operation", 0)]
    public async Task A_malformed_owner_reply_cannot_register_even_a_real_program(string operation, long generation)
    {
        await using var f = await Fixture.Start(invalidReply: current => new ChallengeReply(operation, generation,
            null, null, null, false, current.TerminalId));
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var result = await ApiTests.SocketRequestAsync(socket, 1, "providers", "challenge",
            JsonSerializer.Serialize(new { operationId = "operation", machine = f.Hosts.Remote.Runtime.Machine }));
        Assert.Equal("signin_input_failed", result.GetProperty("code").GetString());
        var error = await Assert.ThrowsAsync<CallFailed>(() => f.Hosts.ConnectedMachine.Terminals.CallAsync("read",
            new ReadArgs(f.Program.Id, 0), TerminalJson.Default.ReadArgs, TerminalJson.Default.OutputPiece, default));
        Assert.Equal("source_changed", error.Reason);
        Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly TwoHosts Hosts = new(forwardCommands: true);
        internal readonly ChallengeBoard Board = new(), LocalBoard = new();
        internal readonly FakePtyStarter RemotePty = new(), LocalPty = new();
        internal readonly string Folder = Directory.CreateTempSubdirectory("assembly-inert-challenge-").FullName;
        internal readonly BackgroundWork Work = new(TimeProvider.System, TextWriter.Null);
        internal TerminalHost Owner = null!, Local = null!;
        internal Counted OwnerCalls = null!;
        internal RoutedTerminals Routed = null!;
        internal SignInRelay Relay = null!;
        internal ProgramTerminal Program = null!;
        internal Challenge Challenge = null!;
        internal static async Task<Fixture> Start(Func<string, CallContext, Task>? reading = null,
            Func<Challenge, ChallengeReply>? invalidReply = null,
            Func<string, CallContext, Task>? macReading = null, CallPeer? macPeer = null)
        {
            var f = new Fixture();
            try
            {
                f.Owner = new(f.RemotePty, f.Hosts.Remote.Topics, TimeProvider.System, ("/bin/sh", null));
                f.Local = new(f.LocalPty, f.Hosts.Mac.Topics, TimeProvider.System, ("/bin/sh", null));
                f.OwnerCalls = new(f.Owner); f.Relay = new(f.Hosts.Mac.Forwarder!);
                f.Hosts.Remote.AdditionalFeatures = calls =>
                {
                    new TerminalFeature(f.OwnerCalls).Register(calls);
                    if (invalidReply is null)
                        new ProvidersFeature(f.Board, localEngineMachine: f.Hosts.Remote.Runtime.Machine).Register(
                            reading is null ? calls : new MessageReadGate(calls, reading));
                    else
                        calls.Read("providers", "challenge", ProvidersJson.Default.ChallengeArgs, ProvidersJson.Default.ChallengeReply,
                            (_, _) => Task.FromResult(invalidReply(f.Challenge)));
                };
                f.Hosts.Mac.AdditionalFeatures = calls => new ProvidersFeature(f.LocalBoard,
                    localEngineMachine: f.Hosts.Mac.Runtime.Machine, readRemoteChallenge: f.Relay.ReadChallengeAsync,
                    submitRemoteCode: f.Relay.SubmitCodeAsync, acknowledgeRemoteUrl: f.Relay.AcknowledgeUrlAsync).Register(
                        macReading is null && macPeer is null ? calls
                            : new MessageReadGate(calls, macReading ?? ((_, _) => Task.CompletedTask), macPeer));
                await f.Hosts.Start(); await f.Hosts.StartMirror();
                f.Routed = new(f.Local, f.Hosts.Mac.Sessions, new MachineSet(f.Hosts.ConnectedMachine, f.Hosts.ConnectedMachine), f.Hosts.Mac.Topics, f.Work);
                f.Program = await f.Owner.StartProgramAsync(new ProgramStart("/inert/program", [], new Dictionary<string, string?>(), f.Folder, "signin:operation"), default);
                f.Challenge = f.Board.Post("operation", null, null, null, false, f.Program.Id, null);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Routed is not null) await Routed.DisposeAsync();
            await Hosts.DisposeAsync();
            if (Owner is not null) await Owner.DisposeAsync();
            if (Local is not null) await Local.DisposeAsync();
            await Work.DisposeAsync(); Directory.Delete(Folder, true);
        }
    }
    internal sealed class Counted(TerminalHost host) : ITerminals
    {
        internal int Starts, Lists;
        public Task<TerminalInfo> StartAsync(StartArgs args, CancellationToken ct = default) { Starts++; return host.StartAsync(args, ct); }
        public Task<IReadOnlyList<TerminalInfo>> ListAsync(string? owner, CancellationToken ct) { Lists++; return host.ListAsync(owner, ct); }
        public Task<OutputPiece> ReadAsync(string id, long offset, CancellationToken ct) => host.ReadAsync(id, offset, ct);
        public ValueTask WriteAsync(string id, byte[] bytes, CancellationToken ct) => host.WriteAsync(id, bytes, ct);
        public Task ResizeAsync(string id, int cols, int rows, CancellationToken ct) => host.ResizeAsync(id, cols, rows, ct);
        public ValueTask CloseAsync(string id, CancellationToken ct = default) => host.CloseAsync(id, ct);
        public ValueTask CloseOwnerAsync(string owner, CancellationToken ct = default) => host.CloseOwnerAsync(owner, ct);
    }
}
