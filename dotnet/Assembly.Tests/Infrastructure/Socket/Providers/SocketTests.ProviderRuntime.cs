using System.Text.Json;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Agents;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    [Fact]
    public async Task Provider_runtime_registered_read_save_and_revision_topic_share_one_owning_file()
    {
        var home = Directory.CreateTempSubdirectory("assembly-runtime-socket-").FullName;
        try
        {
            var file = Path.Combine(home, "agents.json");
            File.WriteAllText(file, """[{"id":"codex","name":"Codex","vendorFamily":"codex","command":"never-execute","args":[],"env":{"CODEX_HOME":"/fixture/before"}}]""");
            var source = new AgentsFile(file);
            source.BindOwner("test-machine", _topics);
            new ProviderRuntimeCalls("test-machine", source).Register(_calls);
            using var socket = await OpenAsync();

            using (new FileStream(source.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await SendAsync(socket, """{"t":"sub","id":1,"topic":"provider_runtime","key":"test-machine"}""");
                using var state = JsonDocument.Parse(await ReadAsync(socket));
                Assert.Equal("state", state.RootElement.GetProperty("t").GetString());
                Assert.Equal(source.Revision, state.RootElement.GetProperty("items")[0].GetProperty("item").GetProperty("revision").GetString());
            }
            await CallAsync(socket, 2, "providers", "runtimeRead", JsonSerializer.Serialize(new ProviderRuntimeReadArgs("test-machine", "codex"),
                ProviderRuntimeJson.Default.ProviderRuntimeReadArgs));
            using var readFrame = JsonDocument.Parse(await ReadAsync(socket));
            var read = readFrame.RootElement.GetProperty("result").Deserialize(ProviderRuntimeJson.Default.ProviderRuntimeReply)!;
            await CallAsync(socket, 3, "providers", "runtimeSave", JsonSerializer.Serialize(new ProviderRuntimeSaveArgs("test-machine", "codex", read.Revision,
                read.Settings with { ConfigHome = "/fixture/after" }), ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs));

            using var first = JsonDocument.Parse(await ReadAsync(socket));
            using var second = JsonDocument.Parse(await ReadAsync(socket));
            var change = first.RootElement.GetProperty("t").GetString() == "change" ? first.RootElement : second.RootElement;
            var result = first.RootElement.GetProperty("t").GetString() == "result" ? first.RootElement : second.RootElement;
            var saved = result.GetProperty("result").Deserialize(ProviderRuntimeJson.Default.ProviderRuntimeReply)!;
            Assert.Equal("/fixture/after", saved.Settings.ConfigHome);
            Assert.Equal(saved.Revision, change.GetProperty("item").GetProperty("revision").GetString());
            Assert.Equal(["machine", "revision"], change.GetProperty("item").EnumerateObject().Select(property => property.Name).Order());
            Assert.DoesNotContain("/fixture", change.GetRawText());
            Assert.Equal("/fixture/after", source.Read("codex").Settings.ConfigHome);
            await _writer.Count(1).WaitAsync(Wait);
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
