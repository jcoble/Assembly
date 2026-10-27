using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common;

namespace Assembly.Tests.Core;

/// <summary>The tracker for work that runs on its own (TR1): a stop cancels it and waits for it, up to a deadline.</summary>
public sealed class BackgroundWorkTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new();
    private readonly StringWriter _log = new();
    private readonly BackgroundWork _work;

    public BackgroundWorkTests() => _work = new BackgroundWork(_time, _log);

    [Fact]
    public async Task Work_that_honours_the_stop_ends_and_the_stop_gives_0()
    {
        _work.Start("waits", token => Task.Delay(Timeout.Infinite, token));
        Assert.Equal(1, _work.Running);

        Assert.Equal(0, await _work.StopAsync(Deadline).WaitAsync(Wait));
        Assert.Equal(0, _work.Running);
        Assert.Equal("", _log.ToString());
    }

    [Fact]
    public async Task Work_that_ignores_the_stop_is_counted_and_named_at_the_deadline()
    {
        var never = new TaskCompletionSource();
        _work.Start("stuck", _ => never.Task);

        var stopping = _work.StopAsync(Deadline);
        _time.Advance(Deadline - TimeSpan.FromSeconds(1));
        Assert.False(stopping.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1, await stopping.WaitAsync(Wait));
        Assert.Equal($"shutdown: 1 background tasks still running after 5 s: stuck{Environment.NewLine}", _log.ToString());
        never.SetResult();
    }

    [Fact]
    public async Task A_fault_is_logged_by_name_and_a_cancel_by_the_stop_is_not()
    {
        _work.Start("throws", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("broken");
        });
        _work.Start("cancelled", token => Task.Delay(Timeout.Infinite, token));

        Assert.Equal(0, await _work.StopAsync(Deadline).WaitAsync(Wait));
        Assert.Equal($"background throws: broken{Environment.NewLine}", _log.ToString());
    }

    [Fact]
    public async Task Work_started_during_the_wait_is_waited_for()
    {
        var first = new TaskCompletionSource();
        var late = new TaskCompletionSource();
        _work.Start("first", async _ =>
        {
            await first.Task;
            _work.Start("late", _ => late.Task);
        });

        var stopping = _work.StopAsync(Deadline);
        // The first one's end runs here, inline, and starts the late one before it ends.
        first.SetResult();
        Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(200)));
        Assert.Equal(1, _work.Running);

        late.SetResult();
        Assert.Equal(0, await stopping.WaitAsync(Wait));
    }

    // Held before its work runs, so a stop that comes while the work's first part is still running waits for it.
    [Fact]
    public async Task A_stop_during_a_starts_first_part_waits_for_it()
    {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var starting = Task.Run(() => _work.Start("slow start", _ =>
        {
            entered.Set();
            gate.Wait(CancellationToken.None);
            return Task.CompletedTask;
        }));
        Assert.True(entered.Wait(Wait));

        var stopping = _work.StopAsync(Deadline);
        Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(200)));
        gate.Set();
        await starting.WaitAsync(Wait);
        Assert.Equal(0, await stopping.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_start_after_the_stop_gets_a_cancelled_token_and_a_second_stop_waits_for_it()
    {
        Assert.Equal(0, await _work.StopAsync(Deadline).WaitAsync(Wait));
        var cancelled = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource();
        _work.Start("after", async token =>
        {
            cancelled.SetResult(token.IsCancellationRequested);
            await release.Task;
        });

        Assert.True(await cancelled.Task.WaitAsync(Wait));
        Assert.Equal(1, _work.Running);
        var stopping = _work.StopAsync(Deadline);
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        Assert.Equal(0, await stopping.WaitAsync(Wait));
    }
}
