using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class ProviderRuntimeCallsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-runtime-calls-").FullName;
    private string Source => Path.Combine(_home, "agents.json");
    private static readonly ProviderRuntimeSettings Settings = new(null, null, null, []);
    private static CallContext Owner(CancellationToken ct = default) => new(ct, "fixture", Peer: new OwnerPeer());

    private AgentsFile Unbound()
    {
        File.WriteAllText(Source, """[{"id":"codex","name":"Codex","command":"never-execute","args":[],"vendorFamily":"codex"}]""");
        return new(Source);
    }

    [Theory]
    [InlineData(null, "codex")]
    [InlineData("", "codex")]
    [InlineData("engine-7", null)]
    [InlineData("engine-7", "")]
    public async Task Missing_selection_refuses_before_unbound_store_or_file_access(string? machine, string? provider)
    {
        var store = Unbound();
        File.Delete(Source);
        var calls = new ProviderRuntimeCalls("engine-7", store);

        Assert.Equal("bad_args", (await Assert.ThrowsAsync<CallFailed>(() => calls.ReadAsync(new(machine!, provider!), Owner()))).Reason);
        Assert.Equal("bad_args", (await Assert.ThrowsAsync<CallFailed>(() => calls.SaveAsync(new(machine!, provider!, "revision", Settings), Owner()))).Reason);
        Assert.False(File.Exists(Source));
    }

    [Fact]
    public async Task Owner_and_cancellation_gates_run_before_selection_lookup_and_file_access()
    {
        var store = Unbound();
        File.Delete(Source);
        var calls = new ProviderRuntimeCalls("engine-7", store);
        var phone = new CallContext(default, "fixture", Peer: new PhonePeer("phone"));
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<CallFailed>(() => calls.ReadAsync(new("", ""), phone))).Reason);
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<CallFailed>(() => calls.SaveAsync(new("", "", "", Settings), phone))).Reason);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calls.ReadAsync(new("", ""), Owner(stopped.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calls.SaveAsync(new("", "", "", Settings), Owner(stopped.Token)));
        Assert.False(File.Exists(Source));
    }

    [Fact]
    public async Task Missing_unbound_and_wrong_owner_stores_never_reach_the_file()
    {
        var store = Unbound();
        File.Delete(Source);
        foreach (var source in new AgentsFile?[] { null, store })
        {
            var calls = new ProviderRuntimeCalls("engine-7", source);
            Assert.Equal("configuration_unavailable", (await Assert.ThrowsAsync<CallFailed>(() => calls.ReadAsync(new("engine-7", "codex"), Owner()))).Reason);
            Assert.Equal("configuration_unavailable", (await Assert.ThrowsAsync<CallFailed>(() => calls.SaveAsync(new("engine-7", "codex", "revision", Settings), Owner()))).Reason);
        }
        store.BindOwner("another-engine", new Topics());
        var wrong = new ProviderRuntimeCalls("engine-7", store);
        Assert.Equal("wrong_machine", (await Assert.ThrowsAsync<CallFailed>(() => wrong.ReadAsync(new("engine-7", "codex"), Owner()))).Reason);
        Assert.Equal("wrong_machine", (await Assert.ThrowsAsync<CallFailed>(() => wrong.SaveAsync(new("engine-7", "codex", "revision", Settings), Owner()))).Reason);
        Assert.False(File.Exists(Source));
    }

    [Fact]
    public async Task An_owner_without_a_forwarder_refuses_foreign_selection_before_local_store_access()
    {
        var store = Unbound();
        File.Delete(Source);
        var calls = new ProviderRuntimeCalls("engine-7", store);

        Assert.Equal("wrong_machine", (await Assert.ThrowsAsync<CallFailed>(() => calls.ReadAsync(new("foreign-engine", "codex"), Owner()))).Reason);
        Assert.Equal("wrong_machine", (await Assert.ThrowsAsync<CallFailed>(() => calls.SaveAsync(new("foreign-engine", "codex", "revision", Settings), Owner()))).Reason);
        Assert.False(File.Exists(Source));
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
