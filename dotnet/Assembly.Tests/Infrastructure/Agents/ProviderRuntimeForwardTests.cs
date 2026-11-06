using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed partial class ProviderRuntimeForwardTests
{
    [Fact]
    public async Task Public_desktop_calls_use_the_actual_remote_engine_owner_and_never_the_local_descriptor_file()
    {
        var home = Directory.CreateTempSubdirectory("assembly-runtime-forward-").FullName;
        try
        {
            await using var hosts = new TwoHosts(forwardCommands: true);
            var local = Source(Path.Combine(home, "desktop.json"), "/fixture/desktop");
            var remote = Source(Path.Combine(home, "remote.json"), "/fixture/remote");
            local.BindOwner(hosts.Mac.Runtime.Machine, hosts.Mac.Topics);
            remote.BindOwner(hosts.Remote.Runtime.Machine, hosts.Remote.Topics);
            hosts.Mac.AdditionalFeatures = calls => new ProviderRuntimeCalls(hosts.Mac.Runtime.Machine, local, hosts.Mac.Forwarder).Register(calls);
            hosts.Remote.AdditionalFeatures = calls => new ProviderRuntimeCalls(hosts.Remote.Runtime.Machine, remote).Register(calls);
            await hosts.Start();
            await hosts.StartMirror();
            var originalLocal = File.ReadAllBytes(local.Path);
            using var socket = new ClientWebSocket();
            var address = hosts.Mac.Address;
            await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket"), default);
            await Send(socket, """{"t":"hello","token":"isolated-token","protocol":1}""");
            Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());

            var selection = new ProviderRuntimeReadArgs(hosts.Remote.Runtime.Machine, "codex");
            var frame = await Call(socket, 1, "runtimeRead", JsonSerializer.SerializeToElement(selection, ProviderRuntimeJson.Default.ProviderRuntimeReadArgs));
            Assert.Equal("result", frame.GetProperty("t").GetString());
            var current = frame.GetProperty("result").Deserialize(ProviderRuntimeJson.Default.ProviderRuntimeReply)!;
            Assert.Equal(hosts.Remote.Runtime.Machine, current.Machine);
            Assert.Equal("/fixture/remote", current.Settings.ConfigHome);
            var save = new ProviderRuntimeSaveArgs(current.Machine, current.Provider, current.Revision,
                current.Settings with { ConfigHome = "/fixture/remote-saved" });
            var changed = await Call(socket, 2, "runtimeSave", JsonSerializer.SerializeToElement(save, ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs));
            Assert.Equal("result", changed.GetProperty("t").GetString());
            Assert.Equal("/fixture/remote-saved", remote.Read("codex").Settings.ConfigHome);
            Assert.Equal(originalLocal, File.ReadAllBytes(local.Path));

            var displayName = selection with { Machine = "workbox-test" };
            var refused = await Call(socket, 3, "runtimeRead", JsonSerializer.SerializeToElement(displayName, ProviderRuntimeJson.Default.ProviderRuntimeReadArgs));
            Assert.Equal("not_found", refused.GetProperty("code").GetString());
            Assert.Equal(originalLocal, File.ReadAllBytes(local.Path));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    private static AgentsFile Source(string path, string home)
    {
        File.WriteAllText(path, Descriptor(home));
        return new(path);
    }

    private static string Descriptor(string home) => JsonSerializer.Serialize(new[] { new
        {
            id = "codex", name = "Codex", vendorFamily = "codex", command = "never-execute", args = Array.Empty<string>(),
            env = new Dictionary<string, string> { ["CODEX_HOME"] = home }
        } });

    private static async Task<JsonElement> Call(ClientWebSocket socket, int id, string method, JsonElement args)
    {
        await Send(socket, JsonSerializer.Serialize(new { t = "call", id, feature = "providers", method, args }));
        return await Read(socket);
    }

    private static Task Send(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult piece;
        do
        {
            piece = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
            Assert.Equal(WebSocketMessageType.Text, piece.MessageType);
            bytes.Write(buffer, 0, piece.Count);
        } while (!piece.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray());
        return json.RootElement.Clone();
    }
}
