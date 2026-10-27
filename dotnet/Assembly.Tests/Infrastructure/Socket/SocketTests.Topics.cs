using System.Text.Json;
using Assembly.Application.Common.Calls;

namespace Assembly.Tests.Infrastructure.Socket;

/// <summary>Topics over the socket (plan 5.7, B5): <c>sub</c>, <c>unsub</c>, <c>state</c> and <c>change</c>.</summary>
public sealed partial class SocketTests
{
    [Fact]
    public async Task Subscribing_sends_state_then_each_change_published_after_it()
    {
        _calls.Topic("notes", TestJson.Default.Note, note => note.Text, (_, _) => [new Note("a")]);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"notes"}""");
        Assert.Equal("""{"t":"state","sub":1,"items":[{"key":"a","item":{"text":"a"}}]}""", await ReadAsync(socket));
        _topics.Publish("notes", "b", """{"text":"b"}""");

        Assert.Equal("""{"t":"change","sub":1,"op":"upsert","key":"b","item":{"text":"b"}}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_state_frame_carries_each_item_with_its_key()
    {
        _calls.Topic("settings", TestJson.Default.Setting, setting => setting.Key, (_, _) => [new Setting("theme", "dark"), new Setting("font", "mono")]);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"settings"}""");

        Assert.Equal("""{"t":"state","sub":1,"items":[{"key":"theme","item":{"key":"theme","value":"dark"}},{"key":"font","item":{"key":"font","value":"mono"}}]}""",
            await ReadAsync(socket));
    }

    [Fact]
    public async Task Unsubscribing_or_closing_the_socket_stops_changes()
    {
        CallContext? context = null;
        _calls.Topic("notes", TestJson.Default.Note, note => note.Text, (_, call) =>
        {
            context = call;
            return [];
        });
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"notes"}""");
        await ReadAsync(socket);
        await SendAsync(socket, """{"t":"unsub","id":1}""");
        // Frames are read in order, so once this answers, the unsub has been seen.
        await CallAsync(socket, 2, "notes", "echo", "{}");
        await ReadAsync(socket);
        Assert.False(_topics.HasSubscribers("notes"));
        _topics.Publish("notes", null, """{"text":"lost"}""");
        await CallAsync(socket, 3, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":3,"result":{"text":""}}""", await ReadAsync(socket));

        var other = await OpenAsync();
        await SendAsync(other, """{"t":"sub","id":1,"topic":"notes"}""");
        await ReadAsync(other);
        Assert.True(_topics.HasSubscribers("notes"));
        Assert.False(context!.Cancellation.IsCancellationRequested);
        other.Abort();
        other.Dispose();

        await UntilAsync(() => !_topics.HasSubscribers("notes") && context.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task A_subscription_with_no_key_gets_every_change_and_one_with_a_key_only_its_own()
    {
        var keys = new List<string?>();
        _calls.Topic("settings", TestJson.Default.Setting, setting => setting.Key, (key, _) =>
        {
            keys.Add(key);
            return [];
        });
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"settings"}""");
        await SendAsync(socket, """{"t":"sub","id":2,"topic":"settings","key":"theme"}""");
        Assert.Equal("""{"t":"state","sub":1,"items":[]}""", await ReadAsync(socket));
        Assert.Equal("""{"t":"state","sub":2,"items":[]}""", await ReadAsync(socket));
        _topics.Publish("settings", "theme", """{"key":"theme","value":"dark"}""");
        _topics.Publish("settings", "font", """{"key":"font","value":"mono"}""");

        Assert.Equal([null, "theme"], keys);
        Assert.Equal("""{"t":"change","sub":1,"op":"upsert","key":"theme","item":{"key":"theme","value":"dark"}}""", await ReadAsync(socket));
        Assert.Equal("""{"t":"change","sub":2,"op":"upsert","key":"theme","item":{"key":"theme","value":"dark"}}""", await ReadAsync(socket));
        Assert.Equal("""{"t":"change","sub":1,"op":"upsert","key":"font","item":{"key":"font","value":"mono"}}""", await ReadAsync(socket));
        // Nothing else was queued before this answer: the keyed subscription didn't get the font change.
        await CallAsync(socket, 3, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":3,"result":{"text":""}}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_state_over_4_MB_answers_failed_and_subscribes_nothing()
    {
        _calls.Topic("notes", TestJson.Default.Note, note => note.Text, (_, _) => [new Note(new string('x', 4 * 1024 * 1024))]);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"notes"}""");

        Assert.Equal("failed", Code(await ReadAsync(socket), id: 1));
        Assert.False(_topics.HasSubscribers("notes"));
    }

    [Fact]
    public async Task A_sub_to_an_unknown_topic_answers_not_found()
    {
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"missing"}""");

        Assert.Equal("not_found", Code(await ReadAsync(socket), id: 1));
    }

    [Fact]
    public async Task Subscribing_again_on_the_same_socket_or_after_a_reconnect_gives_the_current_state_not_a_replay()
    {
        var value = "a";
        _calls.Topic("notes", TestJson.Default.Note, note => note.Text, (_, _) => [new Note(value)]);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"notes"}""");
        await ReadAsync(socket);
        foreach (var next in new[] { "b", "c" })
        {
            value = next;
            _topics.Publish("notes", null, $$"""{"text":"{{next}}"}""");
            await ReadAsync(socket);
        }
        await SendAsync(socket, """{"t":"unsub","id":1}""");
        await SendAsync(socket, """{"t":"sub","id":2,"topic":"notes"}""");
        Assert.Equal("""{"t":"state","sub":2,"items":[{"key":"c","item":{"text":"c"}}]}""", await ReadAsync(socket));

        using var reconnected = await OpenAsync();
        await SendAsync(reconnected, """{"t":"sub","id":1,"topic":"notes"}""");
        Assert.Equal("""{"t":"state","sub":1,"items":[{"key":"c","item":{"text":"c"}}]}""", await ReadAsync(reconnected));
    }

    // A write committed before the snapshot but published after it reaches the subscriber twice, once in its state
    // and once as a change. A setting is a whole-value upsert, so the second changes nothing (5.7).
    [Fact]
    public async Task A_duplicated_settings_upsert_leaves_the_same_value()
    {
        _calls.Topic("settings", TestJson.Default.Setting, setting => setting.Key, (_, _) => [new Setting("theme", "dark")]);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"settings"}""");
        var state = await ReadAsync(socket);
        _topics.Publish("settings", "theme", """{"key":"theme","value":"dark"}""");
        var change = await ReadAsync(socket);

        // State entries and a change apply the same way: by their key, to their item.
        var values = new Dictionary<string, string>();
        foreach (var entry in JsonDocument.Parse(state).RootElement.GetProperty("items").EnumerateArray().Append(JsonDocument.Parse(change).RootElement))
            values[entry.GetProperty("key").GetString()!] = entry.GetProperty("item").GetProperty("value").GetString()!;
        Assert.Equal(new Dictionary<string, string> { ["theme"] = "dark" }, values);
    }

    // A write that commits and publishes while a subscriber's state is being read is neither lost nor sent before the
    // state: its publish waits on the Topics lock until the state is queued and the subscription registered (5.7).
    [Fact]
    public async Task A_write_published_during_the_initial_read_comes_once_after_the_state()
    {
        var rows = new List<string> { "a" };
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim();
        _calls.Topic("notes", TestJson.Default.Note, note => note.Text, (_, _) =>
        {
            var snapshot = rows.Select(text => new Note(text)).ToList();
            reading.SetResult();
            // A gate that never opens fails the subscribe, rather than letting the read go on unwatched.
            if (!gate.Wait(Wait))
                throw new TimeoutException("the test never opened the gate");
            return snapshot;
        });
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"notes"}""");
        await reading.Task.WaitAsync(Wait);
        // The write commits after the read began, then publishes on its own thread, which blocks on the Topics lock.
        rows.Add("b");
        var publisher = new Thread(() => _topics.Publish("notes", "b", """{"text":"b"}""")) { IsBackground = true };
        publisher.Start();
        await UntilAsync(() => publisher.ThreadState.HasFlag(ThreadState.WaitSleepJoin));
        gate.Set();

        Assert.Equal("""{"t":"state","sub":1,"items":[{"key":"a","item":{"text":"a"}}]}""", await ReadAsync(socket));
        Assert.Equal("""{"t":"change","sub":1,"op":"upsert","key":"b","item":{"text":"b"}}""", await ReadAsync(socket));
        Assert.True(publisher.Join(Wait));
        await CallAsync(socket, 2, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":2,"result":{"text":""}}""", await ReadAsync(socket));
    }

    // Waits for something the backend does on its own after the socket closes, giving up after Wait.
    private static async Task UntilAsync(Func<bool> done)
    {
        using var limit = new CancellationTokenSource(Wait);
        while (!done())
            await Task.Delay(10, limit.Token);
    }
}
