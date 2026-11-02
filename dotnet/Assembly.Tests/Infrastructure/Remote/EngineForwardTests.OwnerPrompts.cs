using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Owner_first_page_omission_pins_existing_identity_and_absent_or_wrong_identity_refuses()
    {
        await using var h = await Started();
        var prompt = OwnerPromptFingerprint.Prepare(h.Remote.Fixture.Machine, new("s", "\0full😀", "C", [], null), default);
        h.Remote.Fixture.Db.Writer.Write("test.busy", s => s.Execute("UPDATE engine_session SET state='running' WHERE id='s'"));
        Assert.True(OwnerAccept(h.Remote, prompt).Applied);
        var args = JsonSerializer.SerializeToElement(new OwnerPromptReadArgs("s"), EngineSocketJson.Default.OwnerPromptReadArgs);
        var result = await h.ConnectedMachine.ReadOwnerPromptAsync("owner_prompts", args, false, null, default);
        var page = result.Deserialize(EngineSocketJson.Default.OwnerPromptPage)!;
        Assert.Equal(h.Remote.Fixture.Machine, page.EngineMachine); Assert.Single(page.Items);
        await Assert.ThrowsAsync<MachineUnreachable>(() => h.ConnectedMachine.ReadOwnerPromptAsync("owner_prompts", args, false, "paired-phone-id", default));
        var sessions = SessionStore.Open(h.Mac.Fixture.Db, h.Mac.Fixture.Time, "/isolated");
        using var hub = new LiveHub(RowStore.Open(h.Mac.Fixture.Db));
        await using var unverified = new RemoteMachine("unverified", h.Remote.Address, "isolated-token", hub, sessions, TextWriter.Null, new MachineStatus(h.Mac.Topics, []), topics: h.Mac.Topics);
        await Assert.ThrowsAsync<MachineUnreachable>(() => unverified.ReadOwnerPromptAsync("owner_prompts", args, false, null, default));
        var chunkArgs = JsonSerializer.SerializeToElement(new OwnerPromptChunkArgs("s", "C", prompt.ContentSha256, "text", 0), EngineSocketJson.Default.OwnerPromptChunkArgs);
        var chunk = (await h.ConnectedMachine.ReadOwnerPromptAsync("owner_prompt_chunk", chunkArgs, true, page.EngineMachine, default)).Deserialize(EngineSocketJson.Default.OwnerPromptChunk)!;
        Assert.Equal(prompt.Text, new UTF8Encoding(false, true).GetString(chunk.Data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_read_in_flight_on_replaced_connection_returns_no_content(bool bulk)
    {
        await using var h = await Started();
        h.Remote.Fixture.Db.Writer.Write("test.busy", s => s.Execute("UPDATE engine_session SET state='running' WHERE id='s'"));
        var prompt = OwnerPromptFingerprint.Prepare(h.Remote.Fixture.Machine, new("s", "private body", "C", [], null), default);
        Assert.True(OwnerAccept(h.Remote, prompt).Applied);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OwnerReading = (_, _) => { entered.TrySetResult(); return release.Task; };
        var method = bulk ? "owner_prompt_chunk" : "owner_prompts";
        var args = bulk ? JsonSerializer.SerializeToElement(new OwnerPromptChunkArgs("s", "C", prompt.ContentSha256, "text", 0), EngineSocketJson.Default.OwnerPromptChunkArgs)
            : JsonSerializer.SerializeToElement(new OwnerPromptReadArgs("s"), EngineSocketJson.Default.OwnerPromptReadArgs);
        var pending = h.ConnectedMachine.ReadOwnerPromptAsync(method, args, bulk, null, default);
        try
        {
            await entered.Task.WaitAsync(Wait); // The actual owning handler proves the payload was sent.
            h.Remote.Endpoint.DropAll();
            h.Remote.Commit(1);
            await h.Reached(h.Remote.Through); // The replacement live socket completed its own identity handshake.
            release.TrySetResult();
            await Assert.ThrowsAsync<MachineUnreachable>(() => pending.WaitAsync(Wait));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Forwarded_manifest_repeats_once_when_actual_outer_reply_id_exceeds_inner_byte_fit()
    {
        await using var h = await Started();
        var machine = h.Remote.Fixture.Machine;
        h.Remote.Fixture.Db.Writer.Write("test.busy", s => s.Execute("UPDATE engine_session SET state='running' WHERE id='s'"));
        var originalCount = 0; var calls = 0; long innerId = 0; Exception? calibrationError = null;
        h.Remote.OwnerReading = async (method, call) =>
        {
            if (method != "owner_prompts" || Interlocked.Increment(ref calls) != 1) return;
            try
            {
                innerId = call.ReplyId!.Value;
                originalCount = await SeedOwnerByteFitAsync(h, innerId);
                return;
            }
            catch (Exception error) { calibrationError = error; throw; }
        };
        using var socket = await Open(h.Mac);
        var args = JsonSerializer.Serialize(new OwnerPromptReadArgs("s", machine, Limit: 200), EngineSocketJson.Default.OwnerPromptReadArgs);
        await Send(socket, $"{{\"t\":\"call\",\"id\":{long.MaxValue},\"feature\":\"engine\",\"method\":\"owner_prompts\",\"args\":{args}}}");
        var frame = await Read(socket); Assert.True(calibrationError is null, calibrationError?.ToString()); Assert.True(frame.GetProperty("t").GetString() == "result", frame.GetRawText());
        var page = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.OwnerPromptPage)!;
        Assert.Equal(2, calls); Assert.Equal(originalCount - 1, page.Items.Count);
        Assert.Equal(page.Items[^1].ClientId, page.NextClientId); Assert.True(OwnerSize(long.MaxValue, page) <= 65536);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_owner_connection_cannot_send_to_same_identity_replacement(bool bulk)
    {
        await using var h = await Started();
        var captured = await h.ConnectedMachine.CaptureOwnerReadAsync(null, default);
        var before = h.Remote.OwnerReadCalls;
        h.Remote.Endpoint.DropAll(); h.Remote.Commit(1); await h.Reached(h.Remote.Through);
        var args = JsonSerializer.SerializeToElement(new OwnerPromptReadArgs("s"), EngineSocketJson.Default.OwnerPromptReadArgs);
        await Assert.ThrowsAsync<MachineUnreachable>(() => h.ConnectedMachine.CallAsync("engine", "owner_prompts", args,
            bulk, default, captured.Identity, captured.Socket));
        Assert.Equal(before, h.Remote.OwnerReadCalls);
    }

    [Fact]
    public async Task Same_identity_reconnect_between_first_page_result_and_repeat_returns_no_content()
    {
        await using var h = await Started();
        h.Remote.Fixture.Db.Writer.Write("test.busy", s => s.Execute("UPDATE engine_session SET state='running' WHERE id='s'"));
        var calls = 0;
        h.Remote.OwnerReading = async (method, call) =>
        {
            if (method == "owner_prompts" && Interlocked.Increment(ref calls) == 1)
                await SeedOwnerByteFitAsync(h, call.ReplyId!.Value);
        };
        var measured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int Measure(OwnerPromptPage page)
        {
            var size = OwnerSize(long.MaxValue, page);
            Assert.True(size > 65536);
            measured.TrySetResult();
            release.Task.WaitAsync(Wait).GetAwaiter().GetResult(); // Deterministic test-only boundary after the actual first result.
            return size;
        }
        var pending = h.ConnectedMachine.ReadOwnerPromptPageAsync(new("s", Limit: 200), Measure, null, default);
        try
        {
            await measured.Task.WaitAsync(Wait);
            h.Remote.Endpoint.DropAll(); h.Remote.Commit(1); await h.Reached(h.Remote.Through);
            release.TrySetResult();
            await Assert.ThrowsAsync<MachineUnreachable>(() => pending.WaitAsync(Wait));
            Assert.Equal(1, calls); // The repeat was rejected before reaching the replacement owner.
        }
        finally { release.TrySetResult(); }
    }

    private static async Task<int> SeedOwnerByteFitAsync(TwoHosts h, long innerId)
    {
        var machine = h.Remote.Fixture.Machine;
        var items = new List<OwnerPromptManifest>();
        OwnerPromptManifest Manifest(int i, string preview) => new(new string('C', 60) + i.ToString("D4"), new string('a', 64), preview, false,
            Encoding.UTF8.GetByteCount(preview) + 1, 2, 0, null, 0, "queued", 1);
        var baseline = new string('a', 256);
        while (true)
        {
            var next = Manifest(items.Count, baseline);
            var bytes = OwnerSize(innerId, new(machine, [.. items, next], 1000, next.ClientId));
            if (bytes > 65536) break;
            items.Add(next);
        }
        Assert.True(items.Count > 1);
        var last = items[^1];
        var delta = 65536 - OwnerSize(innerId, new(machine, items, 1000, last.ClientId));
        var target = OwnerSize(innerId, new(machine, [last], 1000, last.ClientId)) + delta;
        string? chosen = null;
        for (var emoji = 0; emoji <= 256 && chosen is null; emoji++)
            for (var quote = 0; quote <= Math.Min(30, 256 - emoji); quote++)
            {
                var text = string.Concat(Enumerable.Repeat("😀", emoji)) + new string('"', quote) + new string('a', 256 - emoji - quote);
                var candidate = Manifest(items.Count - 1, text);
                if (OwnerSize(innerId, new(machine, [candidate], 1000, candidate.ClientId)) == target) { chosen = text; break; }
            }
        Assert.NotNull(chosen);
        for (var i = 0; i <= items.Count; i++)
        {
            var text = (i == items.Count - 1 ? chosen : baseline) + "!";
            var client = new string('C', 60) + i.ToString("D4");
            Assert.True(OwnerAccept(h.Remote, OwnerPromptFingerprint.Prepare(machine, new("s", text, client, [], null), default)).Applied);
            await h.Reached(h.Remote.Through); // Drain actual pushed changes; do not overload the bounded mirror queue in setup.
        }
        var first = h.Remote.Runtime.Reader.OwnerPrompts(new("s", Limit: 200), p => OwnerSize(innerId, p), default);
        var originalCount = first.Items.Count;
        Assert.Equal(items.Count, originalCount); Assert.Equal(65536, OwnerSize(innerId, first));
        Assert.True(OwnerSize(long.MaxValue, first) > 65536);
        return originalCount;
    }

    private static int OwnerSize(long id, OwnerPromptPage page) => SocketDispatcher.Result(id,
        JsonSerializer.SerializeToElement(page, EngineSocketJson.Default.OwnerPromptPage)).Length;
    private static CommandResult OwnerAccept(TwoHosts.Host host, PreparedOwnerPrompt prompt) => host.Fixture.Db.Writer.Write("test.owner", s =>
        host.Runtime.Commands.ApplyOwnerPrompt(s, new(OwnerPromptIdentity.CommandId(host.Fixture.Machine, prompt.ToSessionId, prompt.ClientId),
            host.Fixture.Machine, new(ActorKind.Window, "owner"), OwnerPromptIdentity.MessageId(prompt.ToSessionId, prompt.ClientId), 0, 0, 1000), prompt));
}
