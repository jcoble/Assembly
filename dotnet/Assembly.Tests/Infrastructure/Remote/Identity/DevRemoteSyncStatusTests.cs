using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Updates;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class DevRemoteSyncStatusTests
{
    [Fact]
    public async Task A_launch_update_reaches_the_machine_without_polling_and_a_finish_rereads_agents()
    {
        var directory = Directory.CreateTempSubdirectory("dev-sync-state-").FullName;
        var path = Path.Combine(directory, "remote-sync-state.json");
        var topics = new Topics();
        var status = new MachineStatus(topics, ["workbox-test"]);
        status.Reachable("workbox-test");
        File.WriteAllText(path, """{"updating":false,"reason":"Failed to start; host may be down."}""");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Replace(string json) { File.WriteAllText(path + ".new", json); File.Move(path + ".new", path, overwrite: true); }
        try
        {
            using var watcher = new DevRemoteSyncStatus(path, status, () => finished.TrySetResult());
            Assert.Contains("Failed to start", status.Items[1].UpdateNote);
            Assert.False(finished.Task.IsCompleted);
            var pushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscription = topics.Subscribe(MachinesFeature.Topic, "workbox-test", _ => { }, (_, json) =>
            {
                using var value = JsonDocument.Parse(json);
                if (value.RootElement.TryGetProperty("updateNote", out var note) && note.GetString() == "Host busy; waiting for idle.") pushed.TrySetResult();
            });
            try
            {
                Replace("""{"updating":true,"reason":"Host busy; waiting for idle."}""");
                await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(status.Items[1].Updating);
                Assert.True(status.Items[1].Reachable);
                Replace("""{"updating":false,"reason":null}""");
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(status.Items[1].Updating);
                Assert.Null(status.Items[1].UpdateNote);
            }
            finally { topics.Unsubscribe(subscription); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
