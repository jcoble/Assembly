using System.Text.Json;
using System.Threading.Channels;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Engine;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class MirrorWindowResetTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task Queued_old_events_cannot_precede_a_replacement_snapshot(long replacementHigh)
    {
        // Separate topic delivery lets this schedule the real runtime publisher deterministically.
        // All state and reads still go through the fixture's actual SQLite database and atomic writer.
        using var f = new EngineFixture();
        await using var runtime = EngineRuntime.Start(f.Db, new TopicSet(), f.Time, new(() => 1, () => 5));
        const string machine = "workbox-test-engine";
        f.Db.Writer.Write("test.mirror", s =>
        {
            MirrorApply.Open(s, machine, "workbox-test", 1000);
            MirrorApply.SnapshotPage(s, machine, [Row(machine, 5, "old")]);
            return MirrorApply.EndSnapshot(s, machine, 5, new("old.changed", "s", 5), 1000);
        });
        var publisher = runtime.PublisherFor(machine);
        var frames = Channel.CreateUnbounded<JsonElement>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = new EngineSocketStream(runtime).Subscribe(41, "mirror", null, null, text =>
        {
            using var json = JsonDocument.Parse(text);
            var frame = json.RootElement.Clone();
            if (frame.GetProperty("t").GetString() == "engine_events" && frame.GetProperty("through").GetInt64() == 6)
            {
                entered.TrySetResult();
                promote.Task.WaitAsync(Wait).GetAwaiter().GetResult();
                // Reset from inside the stalled sender is reentrant on its cursor lock. Event 7 is
                // already queued; this puts promotion exactly between the send and its completion.
                f.Db.Writer.Write("test.replace", s =>
                {
                    MirrorApply.BeginSnapshot(s, machine, 1000);
                    MirrorApply.SnapshotPage(s, machine, [Row(machine, replacementHigh, "replacement")]);
                    MirrorApply.EndSnapshot(s, machine, 0, null, 1000);
                    return MirrorApply.Events(s, machine, 0, replacementHigh,
                        replacementHigh == 0 ? null : new("replacement.changed", "s", replacementHigh),
                        Enumerable.Range(1, (int)replacementHigh).Select(i => Event(machine, i, "replacement.changed")).ToArray(), [], 1000);
                });
                publisher.Reset(replacementHigh, 2);
            }
            frames.Writer.TryWrite(frame);
        }, default, machine: machine);
        try
        {
            var initial = await Read(frames.Reader);
            Assert.Equal(5, initial.GetProperty("through").GetInt64());
            f.Db.Writer.Write("test.old-events", s => MirrorApply.Events(s, machine, 5, 7, new("old.changed", "s", 7),
                [Event(machine, 6, "old.changed"), Event(machine, 7, "old.changed")], [Row(machine, 7, "old")], 1000));
            publisher.Drain(default);
            await entered.Task.WaitAsync(Wait);
            promote.TrySetResult();
            Assert.Equal(6, (await Read(frames.Reader)).GetProperty("through").GetInt64()); // already in flight
            var reset = await Read(frames.Reader);
            Assert.Equal("engine_snapshot", reset.GetProperty("t").GetString());
            Assert.Equal(41, reset.GetProperty("id").GetInt64());
            Assert.Equal(replacementHigh, reset.GetProperty("through").GetInt64());
            Assert.Equal("replacement", Assert.Single(reset.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
            subscription.Acknowledge(replacementHigh);
            Assert.Equal(replacementHigh, publisher.MinimumReaderAck);
            f.Db.Writer.Write("test.new-event", s => MirrorApply.Events(s, machine, replacementHigh, replacementHigh + 1,
                new("replacement.changed", "s", replacementHigh + 1), [Event(machine, replacementHigh + 1, "replacement.changed")], [], 1000));
            publisher.Drain(default);
            var next = await Read(frames.Reader);
            Assert.Equal("engine_events", next.GetProperty("t").GetString());
            Assert.Equal(replacementHigh + 1, next.GetProperty("through").GetInt64());
            Assert.Equal("replacement.changed", Assert.Single(next.GetProperty("events").EnumerateArray()).GetProperty("kind").GetString());
            Assert.False(subscription.Closed.IsCancellationRequested);
            subscription.Acknowledge(replacementHigh + 1);
            Assert.Equal(replacementHigh + 1, publisher.MinimumReaderAck);
        }
        finally
        {
            promote.TrySetResult(); subscription.Dispose(); await subscription.Completion.WaitAsync(Wait);
        }
    }

    private static EngineEvent Event(string machine, long seq, string kind) =>
        new(machine, seq, kind, "session", "s", seq, 1000, false, new("s", null, null, null, "idle", null));
    private static JsonElement Row(string machine, long version, string title)
    {
        using var json = JsonDocument.Parse($$$"""{"entityKind":"session","id":"s","machine":"{{{machine}}}","version":{{{version}}},"title":"{{{title}}}"}""");
        return json.RootElement.Clone();
    }
    private static async Task<JsonElement> Read(ChannelReader<JsonElement> frames) =>
        await frames.ReadAsync().AsTask().WaitAsync(Wait);
}
