using System.Text.Json;
using Assembly.Application.Common.Calls;

namespace Assembly.Tests.Draft.Base;

public sealed partial class MachinesSocketTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Remote_agent_discovery_refuses_raw_or_noncanonical_account_identity(int example)
    {
        await _box.ProbeAsync(default); await _box.ReadMirrorOwnerAsync(default);
        var account = example switch
        {
            0 => "/synthetic/PRIVATE_ACCOUNT_CANARY_7b9f/配置",
            1 => "acct-v1:" + new string('a', 63),
            2 => "acct-v1:" + new string('A', 64),
            3 => "acct-v2:" + new string('a', 64),
            4 => new string('a', 64),
            _ => " acct-v1:" + new string('a', 64)
        };
        _stub.Reply = (feature, method) => feature == "machines" && method == "agents"
            ? JsonSerializer.Serialize(new
            {
                agents = new[] { new { id = "provider/with/slash", name = "Provider",
                targetId = new string('b', 64), accountContext = account, providerTargetId = "provider/with/slash/" + account } }
            }) : null;
        var failure = await Assert.ThrowsAsync<CallFailed>(() => _box.AgentsAsync(default));
        Assert.Equal("incompatible backend", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_agent_discovery_preserves_owning_opaque_identity_without_hashing_or_splitting_it(bool nonempty)
    {
        await _box.ProbeAsync(default); await _box.ReadMirrorOwnerAsync(default);
        var account = nonempty ? "acct-v1:" + new string('a', 64) : "";
        _stub.Reply = (feature, method) => feature == "machines" && method == "agents"
            ? JsonSerializer.Serialize(new
            {
                agents = new[] { new { id = "provider/with/slash", name = "Provider",
                targetId = new string('b', 64), accountContext = account, providerTargetId = "provider/with/slash/" + account } }
            }) : null;
        var target = Assert.Single(await _box.AgentsAsync(default));
        Assert.Equal(account, target.AccountContext);
        Assert.Equal("provider/with/slash/" + account, target.ProviderTargetId);
    }
}
