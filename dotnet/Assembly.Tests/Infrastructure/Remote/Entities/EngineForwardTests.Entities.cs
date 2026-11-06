using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Reading;
using Assembly.Engine.Recording;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Entity_batches_are_bulk_only_and_mixed_owner_reads_use_one_actual_owner()
    {
        await using var h = await Started();
        var machine = h.Remote.Fixture.Machine;
        var attribution = new Attribution("s", null, null, null, 0);
        Assert.True(h.Remote.Fixture.Apply(new ToolStarted(attribution, "provider", "batch-tool", "read", Json("input"), 5),
            "batch-tool", actor: Adapter).Applied);
        await h.Reached(h.Remote.Through);
        // Leave the display mirror at its committed value while the owning read sees the later database snapshot.
        h.Remote.Fixture.Db.Writer.Write("test.owner-read", s => s.Execute("UPDATE engine_session SET title='owning snapshot' WHERE id='s'"));
        Assert.NotEqual("owning snapshot", h.Mac.Runtime.Reader.Entity("session", "s", default, machine)!.Value.GetProperty("title").GetString());
        var args = new EngineEntitiesArgs([new("session", "s"), new("tool_call", "batch-tool")], Machine: machine);
        using var control = await Open(h.Mac);
        await Send(control, BatchCall(1, args));
        var wrongChannel = await Read(control);
        Assert.Equal("not_found", wrongChannel.GetProperty("code").GetString());
        using var bulk = await Open(h.Mac, true);
        await Send(bulk, BatchCall(long.MaxValue, args));
        var frame = await Read(bulk);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        Assert.True(SocketFrameBytes(frame) <= 1024 * 1024);
        var items = frame.GetProperty("result").GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("owning snapshot", items[0].GetProperty("title").GetString());
        Assert.Equal("tool_call", items[1].GetProperty("entityKind").GetString());
        Assert.All(items.EnumerateArray(), item => Assert.Equal(machine, item.GetProperty("machine").GetString()));
        await Send(bulk, BatchCall(2, args with { Machine = "workbox-test" }));
        Assert.Equal("error", (await Read(bulk)).GetProperty("t").GetString());
        await Send(bulk, BatchCall(3, new([new("outbox", "private")])));
        Assert.Equal("bad_args", (await Read(bulk)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Forwarded_continuations_keep_one_owner_bulk_scope_and_refuse_same_identity_replacement()
    {
        await using var h = await Started();
        var machine = h.Remote.Fixture.Machine;
        var refs = new List<EngineEntityRef>();
        var input = Json(Enumerable.Range(0, 2000).ToArray());
        for (var i = 0; i < 60; i++)
        {
            var id = $"batch-{i}";
            Assert.True(h.Remote.Fixture.Apply(new ToolStarted(new("s", null, null, null, 0), "provider", id, "read", input, 10_000),
                id, actor: Adapter).Applied);
            refs.Add(new("tool_call", id));
        }
        await h.Reached(h.Remote.Through);
        using var bulk = await Open(h.Mac, true);
        var request = new EngineEntitiesArgs(refs, Machine: machine);
        var seen = new List<string?>();
        string? cursor = null;
        string? firstCursor = null;
        long call = 0;
        do
        {
            await Send(bulk, BatchCall(++call, request with { After = cursor }));
            var frame = await Read(bulk);
            Assert.Equal("result", frame.GetProperty("t").GetString());
            Assert.True(SocketFrameBytes(frame) <= 1024 * 1024);
            var page = frame.GetProperty("result");
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
            cursor = page.TryGetProperty("next", out var next) ? next.GetString() : null;
            firstCursor ??= cursor;
        } while (cursor is not null);
        Assert.NotNull(firstCursor);
        Assert.Equal(refs.Select(item => item.Id), seen);
        // Normal-owner invalidation aborts its bulk socket too. The same Engine id is not the same connection scope.
        h.Remote.Endpoint.DropAll();
        h.Remote.Commit(1);
        await h.Reached(h.Remote.Through);
        await Send(bulk, BatchCall(++call, request with { After = firstCursor }));
        var refused = await Read(bulk);
        Assert.Equal("bad_args", refused.GetProperty("code").GetString());
        Assert.Equal("invalid_cursor", refused.GetProperty("message").GetString());
        await Send(bulk, BatchCall(++call, request));
        Assert.Equal("result", (await Read(bulk)).GetProperty("t").GetString());
    }

    [Fact]
    public async Task Entity_owner_replacement_during_bulk_open_returns_no_payload()
    {
        await using var h = await Started();
        var machine = h.Remote.Fixture.Machine;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OpeningBulk = () => { opened.TrySetResult(); return release.Task; };
        var forward = new MachineForward(id => id == machine ? h.ConnectedMachine : null);
        var args = JsonSerializer.SerializeToElement(new EngineEntitiesArgs([new("session", "s"), new("tool_call", "missing")]),
            EngineSocketJson.Default.EngineEntitiesArgs);
        var pending = forward.CallAsync(machine, "engine", "entities", args, true, default);
        try
        {
            await opened.Task.WaitAsync(Wait);
            h.Remote.Endpoint.DropAll(); h.Remote.Commit(1); await h.Reached(h.Remote.Through);
            release.TrySetResult();
            Assert.Equal("entities_changed", (await Assert.ThrowsAsync<CallFailed>(() => pending.WaitAsync(Wait))).Message);
        }
        finally { release.TrySetResult(); h.Remote.OpeningBulk = null; }
    }

    private static string BatchCall(long id, EngineEntitiesArgs args) =>
        $"{{\"t\":\"call\",\"id\":{id},\"feature\":\"engine\",\"method\":\"entities\",\"args\":{JsonSerializer.Serialize(args, EngineSocketJson.Default.EngineEntitiesArgs)}}}";

    private static int SocketFrameBytes(JsonElement frame) => System.Text.Encoding.UTF8.GetByteCount(frame.GetRawText());
}
