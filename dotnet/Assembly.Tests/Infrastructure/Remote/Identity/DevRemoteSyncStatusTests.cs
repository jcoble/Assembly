using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Remote.Development;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class DevRemoteSyncStatusTests
{
    [Fact]
    public async Task Initial_failure_and_atomic_replacement_push_to_machines_without_polling()
    {
        var directory = Directory.CreateTempSubdirectory("dev-sync-state-").FullName;
        var path = Path.Combine(directory, "remote-sync-state.json");
        var topics = new Topics();
        var status = new MachineStatus(topics, ["workbox-test"]);
        status.Reachable("workbox-test");
        File.WriteAllText(path, """{"reason":"Failed to start; host may be down."}""");
        try
        {
            using var watcher = new DevRemoteSyncStatus(path, status);
            Assert.Contains("Failed to start", status.Items[1].Reason);
            var pushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscription = topics.Subscribe(MachinesFeature.Topic, "workbox-test", _ => { }, (_, json) =>
            {
                using var value = JsonDocument.Parse(json);
                if (value.RootElement.GetProperty("reason").GetString() == "Host busy; waiting for idle.") pushed.TrySetResult();
            });
            try
            {
                File.WriteAllText(path + ".new", """{"reason":"Host busy; waiting for idle."}""");
                File.Move(path + ".new", path, overwrite: true);
                await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(status.Items[1].Reachable);
            }
            finally { topics.Unsubscribe(subscription); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
