using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Agents;
using Assembly.Tests.Infrastructure.Http;
using Fixture = Assembly.Tests.Infrastructure.Agents.SignInTerminalRelayTests.Fixture;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class SignInInputRelayTests
{
    [Fact]
    public async Task Registered_remote_code_submission_preserves_generation_and_consumes_only_the_remote_board_once()
    {
        await using var f = await Fixture.Start();
        var remote = new Input(); var local = new Input();
        var old = f.Board.Post("operation", null, null, null, true, f.Program.Id, remote);
        var current = f.Board.Post("operation", null, null, null, true, f.Program.Id, remote);
        var localChallenge = f.LocalBoard.Post("operation", null, null, null, true, null, local);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var args = new { operationId = "operation", expectedGeneration = current.Generation, code = "INERT_CODE", machine = f.Hosts.Remote.Runtime.Machine };
        var stale = await ApiTests.SocketRequestAsync(socket, 1, "providers", "submitCode", JsonSerializer.Serialize(args with { expectedGeneration = old.Generation }));
        Assert.Equal("stale_challenge", stale.GetProperty("code").GetString());
        Assert.Empty(remote.Values);
        ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 2, "providers", "submitCode", JsonSerializer.Serialize(args)));
        Assert.Equal(new[] { "INERT_CODE" }, remote.Values);
        var consumed = await ApiTests.SocketRequestAsync(socket, 3, "providers", "submitCode", JsonSerializer.Serialize(args));
        Assert.Equal("challenge_consumed", consumed.GetProperty("code").GetString());
        Assert.Single(remote.Values); Assert.Empty(local.Values);
        Assert.Equal(localChallenge, f.LocalBoard.Current("operation"));
        Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
        Assert.DoesNotContain("INERT_CODE", f.Hosts.MirrorLog.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_remote_URL_acknowledgment_consumes_the_exact_remote_generation_and_leaves_local_consent_pending()
    {
        await using var f = await Fixture.Start();
        const string url = "https://example.invalid/inert?state=exact%2Bvalue";
        var remote = f.Board.RequestUrlConsentAsync("operation", new Uri(url), default).AsTask();
        var local = f.LocalBoard.RequestUrlConsentAsync("operation", new Uri(url), default).AsTask();
        try
        {
            var generation = f.Board.Current("operation")!.Generation;
            using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
            var args = new { operationId = "operation", expectedGeneration = generation, originalUrl = url, machine = f.Hosts.Remote.Runtime.Machine };
            var wrong = await ApiTests.SocketRequestAsync(socket, 1, "providers", "acknowledgeUrl",
                JsonSerializer.Serialize(args with { originalUrl = url.Replace("%2B", "%2b", StringComparison.Ordinal) }));
            Assert.Equal("stale_challenge", wrong.GetProperty("code").GetString());
            Assert.False(remote.IsCompleted); Assert.False(local.IsCompleted);
            ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 2, "providers", "acknowledgeUrl", JsonSerializer.Serialize(args)));
            Assert.True(await remote.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(local.IsCompleted);
            var consumed = await ApiTests.SocketRequestAsync(socket, 3, "providers", "acknowledgeUrl", JsonSerializer.Serialize(args));
            Assert.Equal("challenge_consumed", consumed.GetProperty("code").GetString());
            Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
            Assert.DoesNotContain(url, f.Hosts.MirrorLog.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            f.Board.Clear("operation"); f.LocalBoard.Clear("operation");
            await Observe(remote); await Observe(local);
        }
    }

    [Fact]
    public async Task Nonowner_requests_are_refused_before_any_remote_callback_for_all_three_routes()
    {
        var forwarded = 0;
        await using var f = await Fixture.Start(reading: (_, _) => { Interlocked.Increment(ref forwarded); return Task.CompletedTask; }, macPeer: new PhonePeer("inert-peer"));
        var input = new Input();
        var current = f.Board.Post("operation", null, null, null, true, f.Program.Id, input);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        foreach (var method in new[] { "challenge", "submitCode", "acknowledgeUrl" })
        {
            var args = new { operationId = "operation", expectedGeneration = current.Generation, code = "INERT_CODE", originalUrl = "https://example.invalid/inert", machine = f.Hosts.Remote.Runtime.Machine };
            var frame = await ApiTests.SocketRequestAsync(socket, 1, "providers", method, JsonSerializer.Serialize(args));
            Assert.Equal("forbidden", frame.GetProperty("code").GetString());
        }
        Assert.Equal(0, forwarded); Assert.Empty(input.Values); Assert.Equal(current, f.Board.Current("operation"));
    }

    [Fact]
    public async Task An_actual_cancel_frame_before_dispatch_cannot_deliver_remote_code()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(macReading: async (method, call) =>
        {
            if (method != "submitCode") return;
            using var registration = call.Cancellation.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task;
        });
        var input = new Input(); var current = f.Board.Post("operation", null, null, null, true, f.Program.Id, input);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var pending = ApiTests.SocketRequestAsync(socket, 1, "providers", "submitCode", JsonSerializer.Serialize(new
        { operationId = "operation", expectedGeneration = current.Generation, code = "INERT_CODE", machine = f.Hosts.Remote.Runtime.Machine }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"cancel\",\"id\":1}"), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.TrySetResult();
            Assert.Equal("cancelled", (await pending.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("code").GetString());
            Assert.Empty(input.Values); Assert.Equal(current, f.Board.Current("operation"));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Losing_the_reply_after_remote_consumption_is_not_replayed_on_a_replacement_connection()
    {
        await using var f = await Fixture.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new Input(async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { finished.TrySetResult(); }
        });
        var current = f.Board.Post("operation", null, null, null, true, f.Program.Id, input);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var pending = ApiTests.SocketRequestAsync(socket, 1, "providers", "submitCode", JsonSerializer.Serialize(new
        { operationId = "operation", expectedGeneration = current.Generation, code = "INERT_ONCE", machine = f.Hosts.Remote.Runtime.Machine }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Hosts.DisconnectMirror();
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("error", reply.GetProperty("t").GetString());
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Hosts.ReconnectSaved();
        Assert.Equal(new[] { "INERT_ONCE" }, input.Values);
        Assert.Null(f.Board.Current("operation"));
        Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
    }

    [Fact]
    public async Task A_remote_input_held_before_delivery_cannot_cross_its_original_owner_connection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(reading: async (method, _) =>
        {
            if (method != "submitCode") return;
            entered.TrySetResult(); await release.Task;
        });
        var input = new Input();
        var current = f.Board.Post("operation", null, null, null, true, f.Program.Id, input);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var pending = ApiTests.SocketRequestAsync(socket, 1, "providers", "submitCode", JsonSerializer.Serialize(new
        { operationId = "operation", expectedGeneration = current.Generation, code = "INERT_CODE", machine = f.Hosts.Remote.Runtime.Machine }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Hosts.DisconnectMirror();
            await f.Hosts.ReconnectSaved();
            release.TrySetResult();
            Assert.Equal("error", (await pending.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("t").GetString());
            Assert.Empty(input.Values);
            Assert.Equal(current, f.Board.Current("operation"));
            Assert.Equal(0, f.Hosts.Remote.TerminalConnections);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task Observe(Task<bool> consent)
    { try { await consent.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }

    private sealed class Input(Func<CancellationToken, Task>? delivered = null) : IChallengeInput
    {
        internal readonly List<string> Values = [];
        public async ValueTask SubmitAsync(string code, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Values.Add(code); if (delivered is not null) await delivered(ct); }
    }
}
