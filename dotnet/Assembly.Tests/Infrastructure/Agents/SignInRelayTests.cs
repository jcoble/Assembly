using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class SignInRelayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Checked_auth_commands_and_same_ID_replay_apply_only_on_the_captured_engine_owner()
    {
        await using var h = new TwoHosts(windowCalls: true, forwardCommands: true,
            isConfiguredProvider: (provider, account) => provider == "codex" && account == "");
        await h.Start();
        await h.StartMirror();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(h.Mac.Address) { Scheme = "ws", Path = "/socket" }.Uri, default);
        await Send(socket, """{"t":"hello","token":"isolated-token","protocol":1}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
        var target = h.Remote.Runtime.Machine;
        Assert.NotEqual("workbox-test", target);
        var check = new WindowCommand(Guid.NewGuid(), "codex/", 0, 0, new CheckProvider(), target);
        Assert.True((await Command(socket, check)).Applied);
        var start = new WindowCommand(Guid.NewGuid(), "remote-operation", 0, 0,
            new StartSignIn("codex", "", "browser", null), target);
        var accepted = await Command(socket, start);
        Assert.True(accepted.Applied);
        Assert.Equal(accepted, await Command(socket, start));
        Assert.Equal("starting", h.Remote.Fixture.State("engine_sign_in", "remote-operation"));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE id='begin_sign_in/remote-operation'"));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id=$id",
            new SqlArg("$id", start.CommandId.ToString("N"))));
        Assert.Equal("command_id_conflict", (await Command(socket, start with
        {
            Command = new StartSignIn("codex", "", "different-method", null)
        })).Error);
        var cancel = new WindowCommand(Guid.NewGuid(), start.EntityId, accepted.Version, start.Epoch, new CancelSignIn(), target);
        var cancelled = await Command(socket, cancel);
        Assert.True(cancelled.Applied);
        await h.Reached(cancelled.Seq);
        var mirrored = h.Mac.Runtime.Reader.Entity("sign_in", start.EntityId, default, target)!.Value;
        Assert.Equal(target, mirrored.GetProperty("machine").GetString());
        Assert.Equal("cancelled", mirrored.GetProperty("state").GetString());
        Assert.Equal(0, h.Mac.Fixture.Count("engine_command_receipt"));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_provider"));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_sign_in"));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_outbox"));
    }

    [Fact]
    public async Task Challenge_uses_the_actual_engine_owner_and_preserves_ephemeral_generation_and_URL()
    {
        var board = new ChallengeBoard();
        await using var h = await Started(board);
        var relay = new SignInRelay(h.Mac.Forwarder!);
        Assert.Null(await relay.ChallengeAsync(h.Remote.Runtime.Machine, "operation", default));
        const string original = "https://example.invalid/%2f?fixture=exact%2Bvalue";
        var consent = board.RequestUrlConsentAsync("operation", new Uri(original), default).AsTask();
        try
        {
            var answer = (await relay.ChallengeAsync(h.Remote.Runtime.Machine, "operation", default))!.Value;
            var generation = answer.GetProperty("generation").GetInt64();
            Assert.Equal(board.Current("operation")!.Generation, generation);
            Assert.Equal(original, answer.GetProperty("link").GetString());
            Assert.Equal("operation", answer.GetProperty("operationId").GetString());
            var stale = await Assert.ThrowsAsync<CallFailed>(() => relay.AcknowledgeUrlAsync(h.Remote.Runtime.Machine,
                "operation", generation, original.Replace("%2B", "%2b", StringComparison.Ordinal), default));
            Assert.Equal("stale_challenge", stale.Reason);
            Assert.DoesNotContain(original, stale.Message, StringComparison.Ordinal);
            Assert.False(consent.IsCompleted);
            await relay.AcknowledgeUrlAsync(h.Remote.Runtime.Machine, "operation", generation, original, default);
            Assert.True(await consent.WaitAsync(Wait));
            var consumed = await Assert.ThrowsAsync<CallFailed>(() => relay.AcknowledgeUrlAsync(h.Remote.Runtime.Machine,
                "operation", generation, original, default));
            Assert.Equal("challenge_consumed", consumed.Reason);
            Assert.Null(board.Current("operation"));
            Assert.Equal(0, h.Mac.Fixture.Count("engine_sign_in"));
            Assert.Equal(0, h.Remote.Fixture.Count("engine_sign_in"));
            Assert.DoesNotContain(original, h.MirrorLog.ToString(), StringComparison.Ordinal);
        }
        finally { board.Clear("operation"); }
    }

    [Fact]
    public async Task Replaced_generation_is_refused_and_current_code_is_delivered_once_without_echoing_it()
    {
        var board = new ChallengeBoard();
        await using var h = await Started(board);
        var relay = new SignInRelay(h.Mac.Forwarder!);
        var oldInput = new Input();
        var old = board.Post("operation", null, null, null, true, null, oldInput);
        var input = new Input();
        var current = board.Post("operation", null, null, null, true, null, input);
        const string code = "REMOTE_CODE_CANARY";
        var stale = await Assert.ThrowsAsync<CallFailed>(() => relay.SubmitCodeAsync(h.Remote.Runtime.Machine,
            "operation", old.Generation, code, default));
        Assert.Equal("stale_challenge", stale.Reason);
        Assert.DoesNotContain(code, stale.Message, StringComparison.Ordinal);
        Assert.Empty(oldInput.Values);
        Assert.Empty(input.Values);
        await relay.SubmitCodeAsync(h.Remote.Runtime.Machine, "operation", current.Generation, code, default);
        Assert.Equal([code], input.Values);
        var consumed = await Assert.ThrowsAsync<CallFailed>(() => relay.SubmitCodeAsync(h.Remote.Runtime.Machine,
            "operation", current.Generation, code, default));
        Assert.Equal("challenge_consumed", consumed.Reason);
        Assert.DoesNotContain(code, consumed.Message, StringComparison.Ordinal);
        Assert.Single(input.Values);
        Assert.DoesNotContain(code, h.MirrorLog.ToString(), StringComparison.Ordinal);
        foreach (var host in new[] { h.Mac, h.Remote })
        {
            Assert.Equal(0, host.Fixture.Number("SELECT COUNT(*) FROM engine_event WHERE data_json LIKE $code",
                new SqlArg("$code", "%" + code + "%")));
            Assert.Equal(0, host.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE payload_json LIKE $code",
                new SqlArg("$code", "%" + code + "%")));
        }
    }

    [Fact]
    public async Task Profile_name_is_not_an_engine_target_and_disconnected_owner_has_no_local_fallback()
    {
        var board = new ChallengeBoard();
        await using var h = await Started(board);
        var relay = new SignInRelay(h.Mac.Forwarder!);
        Assert.NotEqual("workbox-test", h.Remote.Runtime.Machine);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => relay.ChallengeAsync("workbox-test", "operation", default));
        await h.DisconnectMirror();
        await Assert.ThrowsAsync<MachineUnreachable>(() => relay.ChallengeAsync(h.Remote.Runtime.Machine, "operation", default));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_command_receipt"));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_sign_in"));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_provider"));
    }

    [Fact]
    public async Task Cancelling_code_submission_reaches_the_same_owners_input_waiter()
    {
        var board = new ChallengeBoard();
        await using var h = await Started(board);
        var relay = new SignInRelay(h.Mac.Forwarder!);
        var input = new WaitingInput();
        var challenge = board.Post("operation", null, null, null, true, null, input);
        using var cancellation = new CancellationTokenSource();
        var pending = relay.SubmitCodeAsync(h.Remote.Runtime.Machine, "operation", challenge.Generation,
            "CANCEL_CODE_CANARY", cancellation.Token);
        try
        {
            await input.Entered.Task.WaitAsync(Wait);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Wait));
            await input.Cancelled.Task.WaitAsync(Wait);
            Assert.Null(board.Current("operation"));
        }
        finally { cancellation.Cancel(); board.Clear("operation"); }
    }

    private static async Task<TwoHosts> Started(ChallengeBoard board)
    {
        var hosts = new TwoHosts(windowCalls: true, forwardCommands: true);
        hosts.Remote.AdditionalFeatures = routes => new ProvidersFeature(board, hosts.Remote.Runtime.Machine).Register(routes);
        try { await hosts.Start(); await hosts.StartMirror(); return hosts; }
        catch { await hosts.DisposeAsync(); throw; }
    }

    private static async Task<CommandResult> Command(WebSocket socket, WindowCommand command)
    {
        var args = JsonSerializer.Serialize(command, EngineSocketJson.Default.WindowCommand);
        await Send(socket, $$"""{"t":"call","id":10,"feature":"engine","method":"command","args":{{args}}}""");
        var frame = await Read(socket);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Deserialize(EngineJson.Default.CommandResult)!;
    }

    private static Task Send(WebSocket socket, string frame) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var deadline = new CancellationTokenSource(Wait);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer, deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, part.MessageType);
            bytes.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray());
        return json.RootElement.Clone();
    }

    private sealed class Input : IChallengeInput
    {
        internal List<string> Values { get; } = [];
        public ValueTask SubmitAsync(string code, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Values.Add(code);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WaitingInput : IChallengeInput
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SubmitAsync(string code, CancellationToken ct)
        {
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }
}
