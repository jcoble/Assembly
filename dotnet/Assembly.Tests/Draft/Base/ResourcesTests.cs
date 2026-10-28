using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Diagnostics;
using Assembly.Infrastructure.Clock;
using Assembly.Infrastructure.Diagnostics;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>
/// The <c>resources</c> topic (RS1): the backend's CPU and memory, summed over its process tree and sampled on the
/// backend clock, here on a fake clock and fake readings. Each tick is waited on through the clock's own event, and
/// each change through the topic's publish callback.
/// </summary>
public sealed class ResourcesTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const long MiB = 1024 * 1024;
    private const int Backend = 100;

    private readonly StoppingTime _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly TopicSet _topics = new();
    private readonly States _states = new();
    private readonly Channel<string> _changes = Channel.CreateUnbounded<string>();
    private readonly Channel<(bool Machines, bool Resources)> _ticks = Channel.CreateUnbounded<(bool, bool)>();
    private BackgroundWork _work = null!;
    private BackendClock _clock = null!;
    private volatile ProcessReading[] _tree = [new(Backend, TimeSpan.FromSeconds(10), 100 * MiB)];
    private int _reads;

    public Task InitializeAsync()
    {
        _work = new BackgroundWork(_time, TextWriter.Null);
        _clock = new BackendClock(_topics, _time, _work);
        var feature = new ResourcesFeature(_topics, _clock.Start, Read, _time);
        _clock.Ticked += (_, resourcesWatched) => feature.Tick(resourcesWatched);
        // After the feature's handler, so a tick read here has been through it.
        _clock.Ticked += (machines, resources) => _ticks.Writer.TryWrite((machines, resources));
        feature.Register(_states);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _work.StopAsync(Wait);

    [Fact]
    public void The_first_sample_at_subscribe_has_no_cpu_yet_and_the_tree_s_memory()
    {
        _tree = [new(Backend, TimeSpan.FromSeconds(10), 100 * MiB), new(200, TimeSpan.FromSeconds(3), 20 * MiB)];

        Assert.Equal(Item(null, 120 * MiB), Subscribe().State);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task After_five_ticks_the_cpu_comes_from_each_process_s_time_since_the_last_sample()
    {
        _tree = [new(Backend, TimeSpan.FromSeconds(10), 100 * MiB), new(200, TimeSpan.FromSeconds(2), 20 * MiB)];
        Subscribe();
        _tree = [new(Backend, TimeSpan.FromSeconds(10.613), 100 * MiB), new(200, TimeSpan.FromSeconds(2.3), 20 * MiB)];

        for (var tick = 1; tick < 5; tick++)
            Assert.Equal((false, true), await TickAsync());
        Assert.Equal(1, _reads);
        Assert.False(_changes.Reader.TryRead(out _));
        await TickAsync();

        // 0.613 s + 0.3 s over 5 s is 18.26% of one core.
        Assert.Equal(Item(18.3, 120 * MiB), await ChangeAsync());
        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task A_change_under_the_threshold_publishes_nothing_and_one_at_the_threshold_publishes()
    {
        Subscribe();
        _tree = [new(Backend, TimeSpan.FromSeconds(10.5), 100 * MiB)];
        await WindowAsync();
        Assert.Equal(Item(10, 100 * MiB), await ChangeAsync());

        // 10.04% rounds to the same 10.0, and memory moves by a byte less than 1 MiB.
        _tree = [new(Backend, TimeSpan.FromSeconds(11.002), 101 * MiB - 1)];
        await WindowAsync();
        Assert.False(_changes.Reader.TryRead(out _));

        _tree = [new(Backend, TimeSpan.FromSeconds(11.502), 101 * MiB)];
        await WindowAsync();
        Assert.Equal(Item(10, 101 * MiB), await ChangeAsync());

        _tree = [new(Backend, TimeSpan.FromSeconds(12.007), 101 * MiB)];
        await WindowAsync();
        Assert.Equal(Item(10.1, 101 * MiB), await ChangeAsync());
        Assert.Equal(5, _reads);
    }

    [Fact]
    public async Task Nobody_subscribed_means_no_reads_and_the_clock_stops_on_the_last_leave()
    {
        // The clock running for another topic reads nothing.
        var machines = _topics.Subscribe("machines", null, _ => { }, (_, _) => { });
        _clock.Start();
        for (var tick = 0; tick < 6; tick++)
            Assert.Equal((true, false), await TickAsync());
        _topics.Unsubscribe(machines);
        Assert.Equal((false, false), await TickAsync());
        await _time.Stopped.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(0, _reads);

        var resources = Subscribe().Subscription;
        Assert.Equal((false, true), await TickAsync());
        _topics.Unsubscribe(resources);
        Assert.Equal((false, false), await TickAsync());
        await _time.Stopped.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task A_child_that_exits_can_t_make_the_cpu_negative_and_a_new_one_counts_from_its_next_sample()
    {
        _tree = [new(Backend, TimeSpan.FromSeconds(10), 100 * MiB), new(200, TimeSpan.FromSeconds(50), 30 * MiB)];
        Subscribe();

        _tree = [new(Backend, TimeSpan.FromSeconds(10.5), 100 * MiB), new(300, TimeSpan.FromSeconds(40), 10 * MiB)];
        await WindowAsync();
        Assert.Equal(Item(10, 110 * MiB), await ChangeAsync());

        _tree = [new(Backend, TimeSpan.FromSeconds(11), 100 * MiB), new(300, TimeSpan.FromSeconds(40.25), 10 * MiB)];
        await WindowAsync();
        Assert.Equal(Item(15, 110 * MiB), await ChangeAsync());
    }

    [LinuxFact]
    public void The_real_reader_sums_this_process_and_lists_a_child_it_started()
    {
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var tree = ProcessTree.Read();

            Assert.Contains(tree, reading => reading.Pid == Environment.ProcessId);
            Assert.Contains(tree, reading => reading.Pid == child.Id);
            // Each reading has its resident memory; a collection between two reads moves this process's, so no exact sum.
            Assert.All(tree.Where(reading => reading.Pid == Environment.ProcessId || reading.Pid == child.Id),
                reading => Assert.True(reading.MemoryBytes > 0, $"pid {reading.Pid} reads {reading.MemoryBytes} bytes"));
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    private IReadOnlyList<ProcessReading> Read()
    {
        Interlocked.Increment(ref _reads);
        return _tree;
    }

    private (string State, TopicSet.Subscription Subscription) Subscribe()
    {
        IReadOnlyList<string> state = [];
        var subscription = _topics.Subscribe(ResourcesFeature.Topic, "backend", _ => state = _states.State(ResourcesFeature.Topic, "backend"),
            (_, json) => _changes.Writer.TryWrite(json));
        return (Assert.Single(state), subscription);
    }

    // Five ticks, the fifth of which samples.
    private async Task WindowAsync()
    {
        for (var tick = 0; tick < 5; tick++)
            Assert.Equal((false, true), await TickAsync());
    }

    private async Task<(bool, bool)> TickAsync()
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return await _ticks.Reader.ReadAsync().AsTask().WaitAsync(Wait);
    }

    private async Task<string> ChangeAsync() => await _changes.Reader.ReadAsync().AsTask().WaitAsync(Wait);

    private static string Item(double? cpu, long memory) =>
        $"{{\"cpuPercent\":{(cpu is { } value ? value.ToString(CultureInfo.InvariantCulture) : "null")},\"memoryBytes\":{memory}}}";

    // Keeps each topic's state, so a test can subscribe the way the socket does, without one.
    private sealed class States : ICallRegistry
    {
        private readonly Dictionary<string, Func<string?, IReadOnlyList<string>>> _states = [];

        public IReadOnlyList<string> State(string topic, string? key) => _states[topic](key);

        public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
            Func<string?, CallContext, IReadOnlyList<TItem>> state) =>
            _states[topic] = subKey => [.. state(subKey, new CallContext(CancellationToken.None, "test")).Select(one => JsonSerializer.Serialize(one, item))];

        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) => throw new NotSupportedException();

        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false) => throw new NotSupportedException();

        public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) =>
            throw new NotSupportedException();

        public void Closed(Action<string> connection) => throw new NotSupportedException();

        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => throw new NotSupportedException();
    }

    // A fake clock that reports each timer it stops, so a test can wait for the backend clock to stop.
    private sealed class StoppingTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public Channel<bool> Stopped { get; } = Channel.CreateUnbounded<bool>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Reported(base.CreateTimer(callback, state, dueTime, period), Stopped);

        private sealed class Reported(ITimer timer, Channel<bool> stopped) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                timer.Dispose();
                stopped.Writer.TryWrite(true);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
