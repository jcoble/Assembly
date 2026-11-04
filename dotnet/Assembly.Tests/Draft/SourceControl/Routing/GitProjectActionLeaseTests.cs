using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitProjectActionLeaseTests
{
    [Fact]
    public async Task Duplicate_project_action_disposes_only_its_unused_capture_and_the_execution_owns_the_original()
    {
        var topics = new Topics();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var operations = new GitOperations(work, topics, TimeProvider.System, "owner");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new Lease(); var duplicate = new Lease(); var conflicting = new Lease(); var invalid = new Lease();
        var request = new GitOperationStart(operations.InstanceId, Guid.NewGuid(), null, "draftReview", JsonDocument.Parse("{}").RootElement.Clone(),
            ExpectedSource: new("owner", 0, null), ExpectedCheckoutRoot: "/owned", ProjectId: "project", ExpectedRepository: "owner/repo");
        operations.Observe(request.OperationId, "fixture");
        operations.Start(request, async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return JsonDocument.Parse("{}").RootElement.Clone(); }, original);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var repeated = operations.Start(request, _ => throw new InvalidOperationException("Must not replay"), duplicate);
        Assert.Equal("running", repeated.State);
        Assert.True(duplicate.Disposed.Task.IsCompleted);
        Assert.False(original.Disposed.Task.IsCompleted);
        Assert.Equal("operation_id_conflict", Assert.Throws<CallFailed>(() => operations.Start(request with { ProjectId = "other" }, _ => throw new InvalidOperationException(), conflicting)).Message);
        Assert.True(conflicting.Disposed.Task.IsCompleted);
        Assert.Throws<CallFailed>(() => operations.Start(request with { OperationId = Guid.Empty }, _ => throw new InvalidOperationException(), invalid));
        Assert.True(invalid.Disposed.Task.IsCompleted);
        release.TrySetResult();
        await original.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("succeeded", operations.Status(request.OperationId).State);
    }
    private sealed class Lease : IDisposable
    {
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }
}
