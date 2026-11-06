using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitOperationTests
{
    // Pins: Task0 subscribe-before-start, stable UUID deduplication, push without polling.
    [Fact]
    public async Task One_live_uuid_executes_once_and_pushes_versioned_completion_outside_the_state_lock()
    {
        var time = new FakeTimeProvider();
        var topics = new TopicSet();
        await using var work = new BackgroundWork(time, TextWriter.Null);
        await using var operations = new GitOperations(work, topics, time, "owner");
        var id = Guid.NewGuid();
        var done = new TaskCompletionSource<GitOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var versions = new List<long>();
        using var watch = topics.Subscribe("gitOperation", operations.Key(id), _ => operations.Observe(id, "connection"), (_, json) =>
        {
            var status = JsonSerializer.Deserialize(json, GitOperationsJson.Default.GitOperationStatus)!;
            versions.Add(status.Version);
            Assert.Equal(status.Version, operations.Status(id).Version);
            if (status.State == "succeeded") done.TrySetResult(status);
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var request = new GitOperationStart(operations.InstanceId, id, "session", "push", JsonSerializer.SerializeToElement(new { branch = "main" }));
        var accepted = operations.Start(request, async ct => { count++; entered.SetResult(); await release.Task.WaitAsync(ct); return JsonSerializer.SerializeToElement(new { message = "Pushed" }); });
        Assert.Equal("accepted", accepted.State);
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal("running", operations.Status(id).State);
        Assert.Equal("running", operations.Start(request, _ => throw new InvalidOperationException("must not execute")).State);
        Assert.Equal("operation_id_conflict", Assert.Throws<CallFailed>(() => operations.Start(request with { Args = JsonSerializer.SerializeToElement(new { branch = "other" }) }, _ => throw new InvalidOperationException())).Message);
        release.SetResult();
        Assert.Equal("Pushed", (await done.Task.WaitAsync(TimeSpan.FromSeconds(15))).Result!.Value.GetProperty("message").GetString());
        Assert.Equal(1, count);
        Assert.Equal(new long[] { 1, 2, 3 }, versions);
        operations.Release(id);
        Assert.Equal("uncertain", operations.Status(id).State);
    }

    // Pins: New Project companion I2; explicit init dispatch blocks user cancellation through actual completion.
    [Fact]
    public async Task Dispatched_initialization_refuses_user_cancel_and_remains_owned_after_the_view_detaches()
    {
        var time = new FakeTimeProvider(); var topics = new TopicSet();
        await using var work = new BackgroundWork(time, TextWriter.Null);
        await using var operations = new GitOperations(work, topics, time, "owner");
        var id = Guid.NewGuid();
        operations.Observe(id, "connection");
        var dispatched = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.Start(new(operations.InstanceId, id, null, "createProject", JsonSerializer.SerializeToElement(new { kind = "git" })), async _ =>
        {
            var safety = operations.InitializationDispatched(id); dispatched.SetResult(safety);
            await release.Task.WaitAsync(safety);
            continued.SetResult();
            return JsonSerializer.SerializeToElement(new { continued = true });
        });
        var token = await dispatched.Task;
        Assert.Equal("Repository initialization is already running. It will finish or reach its safety deadline.", Assert.Throws<CallFailed>(() => operations.Cancel(id)).Message);
        Assert.False(token.IsCancellationRequested);
        operations.Closed("connection");
        release.SetResult();
        await continued.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await operations.DisposeAsync();
        Assert.Equal("uncertain", operations.Status(id).State);
    }

    // Pins: Task0 owning deadline, no automatic replay of an uncertain mutation.
    [Fact]
    public async Task Deadline_cancels_and_joins_the_exact_operation_and_old_instances_do_not_execute()
    {
        var time = new FakeTimeProvider(); var topics = new TopicSet();
        await using var work = new BackgroundWork(time, TextWriter.Null);
        await using var operations = new GitOperations(work, topics, time, "owner");
        var id = Guid.NewGuid(); operations.Observe(id, "connection");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<GitOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = topics.Subscribe("gitOperation", operations.Key(id), _ => { }, (_, json) =>
        {
            var status = JsonSerializer.Deserialize(json, GitOperationsJson.Default.GitOperationStatus)!;
            if (status.State == "uncertain") ended.TrySetResult(status);
        });
        var request = new GitOperationStart(operations.InstanceId, id, "session", "push", JsonSerializer.SerializeToElement(new { }));
        operations.Start(request, async ct => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return default; });
        await entered.Task; time.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal("uncertain", (await ended.Task.WaitAsync(TimeSpan.FromSeconds(15))).State);
        Assert.Equal("stale_instance", Assert.Throws<CallFailed>(() => operations.Start(request with { InstanceId = "previous" }, _ => throw new InvalidOperationException())).Message);
        await operations.DisposeAsync();
        Assert.Equal(0, await work.StopAsync(TimeSpan.FromSeconds(5)));
    }
}
