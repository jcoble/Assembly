using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Contracts;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class SignInCallbackForwardTests
{
    [Fact]
    public async Task Registered_callback_waits_for_actual_readiness_and_ACK_keeps_it_until_the_engine_finishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var read = f.Read(socket, 1); await entered.Task.WaitAsync(Fixture.Wait);
        Assert.False(read.IsCompleted); Assert.Null(f.Forward);
        release.TrySetResult();
        var reply = ApiTests.Result(await read.WaitAsync(Fixture.Wait));
        Assert.True(reply.GetProperty("ready").GetBoolean()); Assert.Equal(f.Url, reply.GetProperty("link").GetString());
        var opened = f.Forward!; Assert.True(opened.Pid > 0);
        ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 2, "providers", "acknowledgeUrl", JsonSerializer.Serialize(new
        { operationId = "operation", expectedGeneration = reply.GetProperty("generation").GetInt64(), originalUrl = f.Url, machine = f.Hosts.Remote.Runtime.Machine })));
        Assert.True(await f.Consent.WaitAsync(Fixture.Wait));
        Assert.Equal(JsonValueKind.Null, ApiTests.Result(await f.Read(socket, 3)).ValueKind);
        Assert.False(opened.Completion.IsCompleted); Assert.True(opened.Pid > 0);
        await f.End(); await opened.Completion.WaitAsync(Fixture.Wait);
        Assert.Equal(0, opened.Pid); Assert.False(opened.Lost.IsCompleted);
        await f.Relay.JoinOperationAsync(f.Hosts.Remote.Runtime.Machine, "operation").WaitAsync(Fixture.Wait);
        Assert.Equal(0, f.Relay.RetainedOperationCount);
        Assert.Equal("error", (await f.Read(socket, 4)).GetProperty("t").GetString());
        Assert.Equal(1, f.Opens); Assert.Equal(0, f.Relay.RetainedOperationCount);
    }

    [Fact]
    public async Task A_new_code_generation_at_the_same_callback_reuses_the_owned_forward_and_closing_a_view_does_not_stop_it()
    {
        await using var f = await Fixture.Start();
        using (var first = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token"))
            Assert.True(ApiTests.Result(await f.Read(first, 1)).GetProperty("ready").GetBoolean());
        var owned = f.Forward!;
        var next = f.Board.RequestUrlConsentAsync("operation", new Uri(f.Url), default).AsTask();
        try
        {
            Assert.False(await f.Consent.WaitAsync(Fixture.Wait));
            using var second = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
            var reply = ApiTests.Result(await f.Read(second, 1));
            Assert.Equal(f.Board.Current("operation")!.Generation, reply.GetProperty("generation").GetInt64());
            Assert.True(reply.GetProperty("ready").GetBoolean()); Assert.Same(owned, f.Forward); Assert.Equal(1, f.Opens);
            Assert.True(owned.Pid > 0); Assert.False(owned.Completion.IsCompleted);
        }
        finally { f.Board.Clear("operation"); await next.WaitAsync(Fixture.Wait); }
    }

    [Fact]
    public async Task Cancelling_a_reader_during_setup_only_detaches_and_reopening_joins_the_same_attempt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var read = f.Read(socket, 1); await entered.Task.WaitAsync(Fixture.Wait);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"cancel\",\"id\":1}"), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
        Assert.Equal("error", (await read.WaitAsync(Fixture.Wait)).GetProperty("t").GetString());
        Assert.Null(f.Forward); Assert.Equal(1, f.Opens);
        var reopened = f.Read(socket, 2);
        release.TrySetResult();
        var reply = ApiTests.Result(await reopened.WaitAsync(Fixture.Wait));
        Assert.True(reply.GetProperty("ready").GetBoolean()); Assert.Equal(1, f.Opens);
        Assert.True(f.Forward!.Pid > 0);
        Assert.Equal("starting", f.Hosts.Remote.Runtime.Reader.Entity("sign_in", "operation", default)!.Value.GetProperty("state").GetString());
        Assert.Equal(0, f.Hosts.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='cancel_sign_in'"));
        await f.End(); await f.Relay.JoinOperationAsync(f.Hosts.Remote.Runtime.Machine, "operation").WaitAsync(Fixture.Wait);
        Assert.Equal(0, f.Forward.Pid); Assert.Equal(0, f.Relay.RetainedOperationCount);
    }

    [Fact]
    public async Task A_late_forward_after_profile_invalidation_is_joined_without_publishing_its_URL()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(async _ => { entered.TrySetResult(); await release.Task; }, ignoreOpenCancellation: true);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var read = f.Read(socket, 1); await entered.Task.WaitAsync(Fixture.Wait);
        await f.Hosts.DisconnectMirror(); release.TrySetResult();
        var reply = await read.WaitAsync(Fixture.Wait); Assert.Equal("error", reply.GetProperty("t").GetString());
        var late = await f.Opened.Task.WaitAsync(Fixture.Wait); await late.Completion.WaitAsync(Fixture.Wait);
        Assert.Equal(0, late.Pid);
        await f.Hosts.ReconnectSaved();
        Assert.Equal("error", (await f.Read(socket, 2)).GetProperty("t").GetString()); Assert.Equal(1, f.Opens);
    }

    [Fact]
    public async Task An_actual_forward_exit_tombstones_the_operation_instead_of_restarting_or_retargeting()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var f = await Fixture.Start(reading: async (method, call) =>
        {
            if (method != "challenge" || Interlocked.Increment(ref reads) != 2) return;
            entered.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = call.Cancellation.Register(() => cancelled.TrySetCanceled(call.Cancellation));
            await cancelled.Task;
        });
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        ApiTests.Result(await f.Read(socket, 1)); var owned = f.Forward!;
        var waiting = f.Read(socket, 2); await entered.Task.WaitAsync(Fixture.Wait);
        System.Diagnostics.Process.GetProcessById(owned.Pid).Kill(entireProcessTree: true);
        await owned.Lost.WaitAsync(Fixture.Wait); await owned.Completion.WaitAsync(Fixture.Wait);
        Assert.Equal("signin_callback_lost", (await waiting.WaitAsync(Fixture.Wait)).GetProperty("code").GetString()); Assert.Equal(1, f.Opens);
        await f.Relay.DisposeAsync();
        Assert.Equal("cancelled", f.Hosts.Remote.Runtime.Reader.Entity("sign_in", "operation", default)!.Value.GetProperty("state").GetString());
        Assert.Null(f.Hosts.Mac.Runtime.Reader.Entity("sign_in", "operation", default));
    }

    [Fact]
    public async Task Changing_the_callback_joins_the_old_group_before_the_new_one_can_be_ready()
    {
        await using var f = await Fixture.Start();
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        ApiTests.Result(await f.Read(socket, 1)); var old = f.Forward!;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = "https://example.invalid/inert?redirect_uri=" + Uri.EscapeDataString($"http://127.0.0.1:{port}/other");
        var consent = f.Board.RequestUrlConsentAsync("operation", new Uri(url), default).AsTask();
        try
        {
            var reply = ApiTests.Result(await f.Read(socket, 2));
            Assert.Equal(url, reply.GetProperty("link").GetString()); Assert.True(reply.GetProperty("ready").GetBoolean());
            Assert.True(old.Completion.IsCompletedSuccessfully); Assert.Equal(0, old.Pid);
            Assert.NotSame(old, f.Forward); Assert.Equal(2, f.Opens);
        }
        finally { f.Board.Clear("operation"); await consent.WaitAsync(Fixture.Wait); }
    }

    [Fact]
    public async Task Same_generation_readers_keep_the_first_queued_reply_and_input_valid()
    {
        await using var f = await Fixture.Start();
        var first = new CallContext(default, "first-reader");
        var second = new CallContext(default, "second-reader");
        try
        {
            var original = await f.Relay.ReadChallengeAsync(f.Hosts.Remote.Runtime.Machine, "operation", first);
            var repeated = await f.Relay.ReadChallengeAsync(f.Hosts.Remote.Runtime.Machine, "operation", second);
            Assert.Equal(original, repeated);
            first.SourceGuard!.EnsureCurrent(); second.SourceGuard!.EnsureCurrent();
            await f.Relay.AcknowledgeUrlAsync(f.Hosts.Remote.Runtime.Machine, "operation", original!.Generation, f.Url, default);
            Assert.True(await f.Consent.WaitAsync(Fixture.Wait));
            Assert.Equal(1, f.Opens);
        }
        finally { first.SourceGuard?.Dispose(); second.SourceGuard?.Dispose(); }
    }

    [Fact]
    public async Task Readers_joining_setup_and_the_completed_reply_all_finish_without_an_engine_change()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        var first = new CallContext(default, "first-reader");
        var joining = new CallContext(default, "joining-reader");
        var following = new CallContext(default, "following-reader");
        try
        {
            var initial = f.Relay.ReadChallengeAsync(f.Hosts.Remote.Runtime.Machine, "operation", first);
            await entered.Task.WaitAsync(Fixture.Wait);
            var joined = f.Relay.ReadChallengeAsync(f.Hosts.Remote.Runtime.Machine, "operation", joining);
            var next = initial.ContinueWith(async _ => await f.Relay.ReadChallengeAsync(f.Hosts.Remote.Runtime.Machine,
                "operation", following), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            release.TrySetResult();
            var replies = await Task.WhenAll(initial, joined, next).WaitAsync(Fixture.Wait);
            Assert.All(replies, reply => Assert.True(reply!.Ready == true));
            Assert.All(replies, reply => Assert.Equal(replies[0]!.Generation, reply!.Generation));
            Assert.Equal(1, f.Opens);
        }
        finally { first.SourceGuard?.Dispose(); joining.SourceGuard?.Dispose(); following.SourceGuard?.Dispose(); }
    }

    [Fact]
    public async Task An_occupied_callback_port_is_a_safe_distinct_refusal_and_confirms_original_cancellation()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var f = await Fixture.Start(callbackPort: port);
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var reply = await f.Read(socket, 1).WaitAsync(Fixture.Wait);
        Assert.Equal("signin_callback_port_in_use", reply.GetProperty("code").GetString());
        Assert.DoesNotContain(f.Url, reply.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("isolated", reply.GetRawText(), StringComparison.Ordinal);
        Assert.Null(f.Forward);
        Assert.Equal("cancelled", f.Hosts.Remote.Runtime.Reader.Entity("sign_in", "operation", default)!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Losing_the_original_owner_during_setup_reports_unconfirmed_cancellation_without_retargeting()
    {
        Fixture? fixture = null;
        await using var f = await Fixture.Start(async _ =>
        {
            await fixture!.Hosts.DisconnectMirror();
            throw new IOException("INERT_PRIVATE_FAILURE");
        });
        fixture = f;
        using var socket = await ApiTests.OpenOwnerSocketAsync(f.Hosts.Mac.Address, "isolated-token");
        var reply = await f.Read(socket, 1).WaitAsync(Fixture.Wait);
        Assert.Equal("source_changed", reply.GetProperty("code").GetString());
        Assert.Contains("unconfirmed", reply.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("remote deadline", reply.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("INERT_PRIVATE_FAILURE", reply.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(f.Url, reply.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("starting", f.Hosts.Remote.Runtime.Reader.Entity("sign_in", "operation", default)!.Value.GetProperty("state").GetString());
        Assert.Equal(1, f.Opens); Assert.Null(f.Forward);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
        internal readonly TwoHosts Hosts = new(windowCalls: true, forwardCommands: true, isConfiguredProvider: (p, a) => p == "codex" && a == "");
        internal readonly ChallengeBoard Board = new();
        internal SignInRelay Relay = null!;
        internal LoopbackForward? Forward;
        internal readonly TaskCompletionSource<LoopbackForward> Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Opens;
        internal string Url = "";
        internal Task<bool> Consent = null!;
        internal static async Task<Fixture> Start(Func<CancellationToken, Task>? before = null, bool ignoreOpenCancellation = false, int? callbackPort = null, Func<string, CallContext, Task>? reading = null)
        {
            var f = new Fixture();
            try
            {
                f.Hosts.Remote.AdditionalFeatures = calls => new ProvidersFeature(f.Board, f.Hosts.Remote.Runtime.Machine).Register(reading is null ? calls : new MessageReadGate(calls, reading));
                f.Hosts.Mac.AdditionalFeatures = calls => new ProvidersFeature(new(), f.Hosts.Mac.Runtime.Machine,
                    (m, o, c) => f.Relay.ReadChallengeAsync(m, o, c), (a, c) => f.Relay.SubmitCodeAsync(a, c),
                    (a, c) => f.Relay.AcknowledgeUrlAsync(a, c)).Register(calls);
                await f.Hosts.Start(); await f.Hosts.StartMirror();
                f.Relay = new(f.Hosts.Mac.Forwarder!, f.Hosts.Connections, f.Hosts.Mac.Runtime, f.Hosts.Mac.Fixture.Db,
                    async (ssh, host, port, ct) =>
                    {
                        Assert.Equal("isolated", ssh); f.Opens++;
                        if (before is not null) await before(ct);
                        if (f.Forward is { } previous) Assert.True(previous.Completion.IsCompletedSuccessfully);
                        var opened = await LoopbackForward.OpenAsync(ssh, host, port, TextWriter.Null, ignoreOpenCancellation ? default : ct,
                            (forwards, p) => LoopbackForwardTests.Listener(forwards, p));
                        f.Forward = opened; f.Opened.TrySetResult(opened); return opened;
                    });
                var start = f.Hosts.Remote.Runtime.Submit(new(Guid.NewGuid(), f.Hosts.Remote.Runtime.Machine,
                    new(ActorKind.Window, "inert-owner"), "operation", 0, 0, f.Hosts.Remote.Fixture.Time.GetUtcNow().ToUnixTimeMilliseconds()),
                    new StartSignIn("codex", "", null, null));
                Assert.True(start.Applied, start.Error); await f.Hosts.Reached(start.Seq);
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var port = callbackPort ?? ((IPEndPoint)listener.LocalEndpoint).Port;
                f.Url = "https://example.invalid/inert?redirect_uri=" + Uri.EscapeDataString($"http://127.0.0.1:{port}/callback");
                listener.Stop();
                f.Consent = f.Board.RequestUrlConsentAsync("operation", new Uri(f.Url), default).AsTask();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task<JsonElement> Read(System.Net.WebSockets.ClientWebSocket socket, int id) =>
            ApiTests.SocketRequestAsync(socket, id, "providers", "challenge", JsonSerializer.Serialize(new { operationId = "operation", machine = Hosts.Remote.Runtime.Machine }));
        internal async Task End()
        {
            var row = Hosts.Remote.Runtime.Reader.Entity("sign_in", "operation", default)!.Value;
            var ended = Hosts.Remote.Runtime.Submit(new(Guid.NewGuid(), Hosts.Remote.Runtime.Machine, new(ActorKind.Window, "inert-owner"),
                "operation", row.GetProperty("version").GetInt64(), 0, Hosts.Remote.Fixture.Time.GetUtcNow().ToUnixTimeMilliseconds()), new CancelSignIn());
            Assert.True(ended.Applied, ended.Error); await Hosts.Reached(ended.Seq);
        }
        public async ValueTask DisposeAsync()
        {
            if (Relay is not null) await Relay.DisposeAsync();
            Board.Clear("operation"); if (Consent is not null) await Consent.WaitAsync(Wait);
            await Hosts.DisposeAsync();
        }
    }
}
