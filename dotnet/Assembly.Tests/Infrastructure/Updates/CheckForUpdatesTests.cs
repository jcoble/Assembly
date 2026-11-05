using Assembly.Infrastructure.Topics;
using System.Net;
using System.Text;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

/// <summary>
/// The button's call on the real pieces: the checkout's source, the one check, and the reader a remote answers with, on a
/// bundle in a throwaway home. Only npm's answers stand in.
/// </summary>
public sealed class CheckForUpdatesTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("check-for-updates-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private sealed class Npm(string? version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var package = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/').Replace("/latest", "", StringComparison.Ordinal));
            var answer = version is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"name":"{{package}}","version":"{{version}}"}""", Encoding.UTF8, "application/json") };
            answer.RequestMessage = request;
            return Task.FromResult(answer);
        }
    }

    private void Bundle(string version)
    {
        var bundle = Path.Combine(_home, "assembly-host/adapters/bundles/one");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, InstalledCalls.Manifest),
            "{\"packaging\":\"" + BackendBuild.AdapterSources + "\",\"versions\":{\"claude-agent-acp\":\"" + version + "\",\"codex-acp\":\"" + version + "\"}}");
        File.WriteAllText(Path.Combine(bundle, "entry.js"), "");
    }

    private async Task<IReadOnlyList<UpdateItem>> Press(string? npm)
    {
        var status = new MachineStatus(new Topics(), ["box"]);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var entry = Path.Combine(_home, "assembly-host/adapters/bundles/one/entry.js");
        var reader = new InstalledCalls(new AgentsOnlyLauncher(() => [new AgentDescriptor("a", "A", "node", [entry], null)]), TimeProvider.System);
        var source = UpdateSources.ForHost(new HttpClient(new Npm(npm)), TimeProvider.System, checkout: _home, home: _home);
        var updates = new MachineUpdates(source, status, reader, async (_, ct) => await reader.ReadAsync(ct), null, work, TextWriter.Null);
        await updates.CheckNowAsync("box", default);
        return status.Items[1].Updates!;
    }

    private static string[] Named(IEnumerable<UpdateItem> items, string state) =>
        [.. items.Where(item => item.Kind == "adapter" && item.State == state).Select(item => item.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task It_names_what_is_behind_then_nothing_once_the_bundle_has_caught_up()
    {
        Bundle("1.0.0");
        Assert.Equal(["claude-agent-acp", "codex-acp"], Named(await Press("2.0.0"), UpdateItem.Behind));
        Bundle("2.0.0");
        Assert.Empty(Named(await Press("2.0.0"), UpdateItem.Behind));
    }

    [Fact]
    public async Task What_npm_would_not_say_is_unknown_never_behind()
    {
        Bundle("1.0.0");
        var items = await Press(null);
        Assert.Empty(Named(items, UpdateItem.Behind));
        Assert.DoesNotContain(items, item => item.State == UpdateItem.Behind && item.Kind == "cli");
    }
}
