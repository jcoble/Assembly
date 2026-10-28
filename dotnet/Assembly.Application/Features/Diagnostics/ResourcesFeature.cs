using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;

namespace Assembly.Application.Features.Diagnostics;

/// <summary>
/// The <c>resources</c> topic, key <c>backend</c>: the CPU and resident memory of the backend and every process under
/// it, for the window's utility strip. It rides the backend clock rather than a timer of its own. A subscribe starts
/// the clock and is given the item last sent, if any: it never reads /proc itself, since it runs inside the topics'
/// lock. The first tick with a subscriber and nothing sent yet samples and sends; after that every 5th tick samples,
/// and a change goes out only when the CPU moves by 0.1 or the memory by 1 MiB. Ticks with nobody subscribed read nothing and forget the last sample, so the next
/// subscriber starts a fresh window.
/// </summary>
internal sealed class ResourcesFeature : IFeature
{
    public const string Topic = "resources";

    private const string Backend = "backend";
    private const int Every = 5;
    private const long MiB = 1024 * 1024;

    private readonly ITopics _topics;
    private readonly Action _startClock;
    private readonly Func<IReadOnlyList<ProcessReading>> _read;
    private readonly TimeProvider _time;
    // The last sample's CPU time per pid and when it was taken, and the item last sent; all null while nobody watches.
    private readonly Lock _gate = new();
    private Dictionary<int, TimeSpan>? _cpu;
    private long _taken;
    private ResourceUsage? _sent;
    private int _ticks;

    /// <param name="startClock">Starts the backend clock, whose ticks come back through <see cref="Tick"/>.</param>
    /// <param name="read">Reads the backend's process tree.</param>
    public ResourcesFeature(ITopics topics, Action startClock, Func<IReadOnlyList<ProcessReading>> read, TimeProvider time)
    {
        _topics = topics;
        _startClock = startClock;
        _read = read;
        _time = time;
    }

    public void Register(ICallRegistry calls) =>
        calls.Topic(Topic, ResourcesJson.Default.ResourceUsage, _ => Backend, (key, _) =>
        {
            if (key is not (null or Backend))
                throw new CallFailed($"resources has no {key}; it has {Backend}");
            _startClock();
            lock (_gate)
                return _sent is { } sent ? [sent] : [];
        });

    /// <summary>Each second of the backend clock, with whether <c>resources</c> has a subscriber.</summary>
    public void Tick(bool watched)
    {
        ResourceUsage? changed = null;
        lock (_gate)
        {
            if (!watched)
            {
                (_cpu, _sent) = (null, null);
                return;
            }
            if (++_ticks < Every && _sent is not null)
                return;
        }
        // Outside every lock: a subscribe that holds the topics' lock takes this one, and /proc is slow to scan.
        var readings = _read();
        lock (_gate)
        {
            var usage = Sample(readings);
            // Both CPU values are rounded to 0.1, so any difference is a move of at least 0.1.
            if (_sent is null || usage.CpuPercent != _sent.CpuPercent || Math.Abs(usage.MemoryBytes - _sent.MemoryBytes) >= MiB)
                changed = _sent = usage;
        }
        // Outside the lock: the subscribe holds the topics' lock while it takes this one.
        if (changed is not null)
            _topics.Publish(Topic, Backend, JsonSerializer.Serialize(changed, ResourcesJson.Default.ResourceUsage));
    }

    // CPU is each pid's time since the last sample, so a pid that exited adds nothing and a new one counts from its
    // second sample. A smaller time than before is a reused pid, also nothing.
    private ResourceUsage Sample(IReadOnlyList<ProcessReading> readings)
    {
        var now = _time.GetTimestamp();
        double? cpu = null;
        if (_cpu is { } before)
        {
            var used = readings.Sum(reading =>
                before.TryGetValue(reading.Pid, out var then) && reading.Cpu > then ? (reading.Cpu - then).TotalSeconds : 0);
            cpu = Math.Round(used / _time.GetElapsedTime(_taken, now).TotalSeconds * 100, 1);
        }
        _cpu = readings.ToDictionary(reading => reading.Pid, reading => reading.Cpu);
        _taken = now;
        _ticks = 0;
        return new ResourceUsage(cpu, readings.Sum(reading => reading.MemoryBytes));
    }
}
