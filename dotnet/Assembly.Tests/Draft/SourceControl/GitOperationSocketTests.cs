using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Infrastructure.Git;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitOperationSocketTests
{
    [Fact]
    public async Task Captured_owner_topic_delivers_state_and_versioned_completion_then_joins_on_owner_shutdown()
    {
        await using var hosts = new TwoHosts();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var operations = new GitOperations(work, hosts.Remote.Topics, TimeProvider.System, hosts.Remote.Fixture.Machine);
        var id = Guid.NewGuid();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Remote.AdditionalFeatures = registry =>
        {
            registry.Topic("gitOperation", GitOperationsJson.Default.GitOperationStatus,
                status => operations.Key(status.OperationId), (key, call) =>
                {
                    Assert.Equal(operations.Key(id), key);
                    var status = operations.Observe(id, call.Connection);
                    return status is null ? [] : [status];
                });
            registry.Closed(connection => { operations.Closed(connection); closed.TrySetResult(); });
        };
        await hosts.Start(); await hosts.StartMirror();
        var machine = hosts.ConnectedMachine;
        var owner = await machine.CaptureOwnerReadAsync(null, default);
        await using var stream = machine.GitOperationFramesAsync(id, owner.Identity, owner.Socket, default).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("state", stream.Current.GetProperty("t").GetString());
        Assert.Empty(stream.Current.GetProperty("items").EnumerateArray());
        operations.Start(new(operations.InstanceId, id, null, "prepareProjectFolder", JsonSerializer.SerializeToElement(new { kind = "empty" })),
            _ => Task.FromResult(JsonSerializer.SerializeToElement(new { root = "/isolated/folder" })));
        for (var version = 1; version <= 3; version++)
        {
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
            var status = stream.Current.GetProperty("item");
            Assert.Equal(version, status.GetProperty("version").GetInt64());
            if (version == 3) Assert.Equal("succeeded", status.GetProperty("state").GetString());
        }
        // Read is in flight so the iterator's actual finally owns unsubscribe and drain completion.
        var waiting = stream.MoveNextAsync().AsTask();
        await hosts.StopMirror().WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("uncertain", operations.Status(id).State); // The server's closed hook evicts the final observer.
    }
}
