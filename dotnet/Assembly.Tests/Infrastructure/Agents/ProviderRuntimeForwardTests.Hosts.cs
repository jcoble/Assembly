using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Desktop.Serve;
using Assembly.Engine.Effects;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Remote.Serve;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed partial class ProviderRuntimeForwardTests
{
    [Fact]
    public async Task Desktop_host_binds_the_same_descriptor_source_and_exposes_live_owner_settings_without_launching_a_provider()
    {
        var home = Directory.CreateTempSubdirectory("assembly-runtime-desktop-").FullName;
        try
        {
            var source = Source(Path.Combine(home, "selected-agents.json"), "/fixture/desktop-before");
            var launcher = new AdapterLauncher(source, Path.Combine(home, "sessions"), new UnclaimedPermissions());
            var effects = new NoProviderEffects();
            EngineRuntime? runtime = null;
            await using var backend = await DesktopBackend.StartAsync(home, "runtime-host-token", 0, launcher, TextWriter.Null,
                engineEffects: effects, engineStarted: value => runtime = value, agentsFile: source);
            Assert.NotNull(runtime);
            Assert.Equal(runtime.Machine, source.Current().Machine);
            using var socket = await OpenHost(backend.Address);
            var selected = new ProviderRuntimeReadArgs(runtime.Machine, "codex");
            var read = Result(await Call(socket, 1, "runtimeRead", JsonSerializer.SerializeToElement(selected, ProviderRuntimeJson.Default.ProviderRuntimeReadArgs)));
            Assert.Equal("/fixture/desktop-before", read.Settings.ConfigHome);
            var write = new ProviderRuntimeSaveArgs(runtime.Machine, "codex", read.Revision, read.Settings with { ConfigHome = "/fixture/desktop-after" });
            var saved = Result(await Call(socket, 2, "runtimeSave", JsonSerializer.SerializeToElement(write, ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs)));

            Assert.Equal(runtime.Machine, saved.Machine);
            Assert.Equal("/fixture/desktop-after", launcher.Agents.Single().Env!["CODEX_HOME"]);
            Assert.Equal(saved.Revision, source.Revision);
            Assert.Equal(0, effects.Calls);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public async Task A_test_host_without_an_explicit_source_refuses_settings_even_when_an_agents_file_exists()
    {
        var home = Directory.CreateTempSubdirectory("assembly-runtime-unavailable-").FullName;
        try
        {
            var file = Path.Combine(home, "agents.json");
            File.WriteAllText(file, Descriptor("/fixture/do-not-guess"));
            var original = File.ReadAllBytes(file);
            EngineRuntime? runtime = null;
            await using var backend = await DesktopBackend.StartAsync(home, "runtime-host-token", 0, new FakeLauncher(), TextWriter.Null,
                engineEffects: new NoProviderEffects(), engineStarted: value => runtime = value);
            Assert.NotNull(runtime);
            using var socket = await OpenHost(backend.Address);
            var selection = new ProviderRuntimeReadArgs(runtime.Machine, "codex");

            var refused = await Call(socket, 1, "runtimeRead", JsonSerializer.SerializeToElement(selection, ProviderRuntimeJson.Default.ProviderRuntimeReadArgs));

            Assert.Equal("configuration_unavailable", refused.GetProperty("code").GetString());
            Assert.Equal(original, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public async Task Production_remote_host_uses_its_actual_options_source_and_actual_engine_owner_without_an_injected_launcher()
    {
        var home = Directory.CreateTempSubdirectory("assembly-runtime-serve-").FullName;
        using var stop = new CancellationTokenSource();
        Task<int>? serving = null;
        try
        {
            var chosen = Path.Combine(home, "chosen-descriptor.json");
            File.WriteAllText(chosen, Descriptor("/fixture/remote-before"));
            var token = Path.Combine(home, "token");
            File.WriteAllText(token, "ASSEMBLY_TOKEN=runtime-host-token\n");
            var effects = new NoProviderEffects();
            var ready = new TaskCompletionSource<(Uri Address, EngineRuntime Runtime)>(TaskCreationOptions.RunContinuationsAsynchronously);
            serving = ServeRun.RunAsync(new(0, home, token, chosen), null, effects,
                (address, runtime) => ready.TrySetResult((address, runtime)), stop.Token);
            if (await Task.WhenAny(ready.Task, serving).WaitAsync(TimeSpan.FromSeconds(15)) == serving) await serving;
            var host = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var socket = await OpenHost(host.Address);
            var selected = new ProviderRuntimeReadArgs(host.Runtime.Machine, "codex");
            var read = Result(await Call(socket, 1, "runtimeRead", JsonSerializer.SerializeToElement(selected, ProviderRuntimeJson.Default.ProviderRuntimeReadArgs)));
            Assert.Equal(host.Runtime.Machine, read.Machine);
            Assert.Equal("/fixture/remote-before", read.Settings.ConfigHome);
            var write = new ProviderRuntimeSaveArgs(host.Runtime.Machine, "codex", read.Revision, read.Settings with { ConfigHome = "/fixture/remote-after" });
            var saved = Result(await Call(socket, 2, "runtimeSave", JsonSerializer.SerializeToElement(write, ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs)));
            var stored = AgentDescriptor.Parse(File.ReadAllText(chosen)).Single();

            Assert.Equal(host.Runtime.Machine, saved.Machine);
            Assert.Equal("/fixture/remote-after", stored.Env!["CODEX_HOME"]);
            Assert.False(File.Exists(Path.Combine(home, "agents.json")));
            Assert.Equal(0, effects.Calls);
        }
        finally
        {
            stop.Cancel();
            if (serving is not null)
                try { await serving.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (OperationCanceledException) { }
            Directory.Delete(home, recursive: true);
        }
    }

    private static ProviderRuntimeReply Result(JsonElement frame)
    {
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Deserialize(ProviderRuntimeJson.Default.ProviderRuntimeReply)!;
    }

    private static async Task<ClientWebSocket> OpenHost(Uri address)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket"), default);
        await Send(socket, """{"t":"hello","token":"runtime-host-token","protocol":1}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
        return socket;
    }

    private sealed class NoProviderEffects : IEngineEffects
    {
        public int Calls;
        public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Unexpected provider or session effect in a settings-only fixture.");
        }
        public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Unexpected provider or session reconciliation in a settings-only fixture.");
        }
    }
}
