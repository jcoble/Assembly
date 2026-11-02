using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Tests.Draft.Base;

public sealed partial class MachinesSocketTests
{
    [Fact]
    public async Task Socket_agent_discovery_preserves_both_distinct_targets_and_explicit_empty_account()
    {
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "machines", "agents", "{}");
        using var local = JsonDocument.Parse(await ReadAsync(socket));
        var item = local.RootElement.GetProperty("result").GetProperty("agents")[0];
        Assert.Equal(["accountContext", "id", "name", "providerTargetId", "targetId"], item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("", item.GetProperty("accountContext").GetString());
        Assert.Equal("claude/", item.GetProperty("providerTargetId").GetString());
        Assert.Equal(new string('a', 64), item.GetProperty("targetId").GetString());

        await _box.ProbeAsync(default); await _box.ReadMirrorOwnerAsync(default);
        await CallAsync(socket, 2, "machines", "agents", "{\"machine\":\"box\"}");
        using var remote = JsonDocument.Parse(await ReadAsync(socket));
        var forwarded = remote.RootElement.GetProperty("result").GetProperty("agents")[0];
        foreach (var field in new[] { "id", "name", "accountContext", "providerTargetId" })
            Assert.Equal(item.GetProperty(field).GetString(), forwarded.GetProperty(field).GetString());
        var wrapped = forwarded.GetProperty("targetId").GetString()!;
        var raw = item.GetProperty("targetId").GetString()!;
        Assert.NotEqual(raw, wrapped);
        Assert.EndsWith(":" + raw, wrapped);
        Assert.Equal(129, wrapped.Length);
    }

    [Theory]
    [InlineData("target-missing")]
    [InlineData("target-null")]
    [InlineData("target-malformed")]
    [InlineData("account-missing")]
    [InlineData("account-null")]
    [InlineData("account-wrong-type")]
    [InlineData("provider-missing")]
    [InlineData("provider-null")]
    [InlineData("provider-inconsistent")]
    [InlineData("item-wrong-type")]
    [InlineData("list-wrong-type")]
    public async Task Remote_agent_discovery_refuses_missing_or_inconsistent_safe_identity(string bad)
    {
        await _box.ProbeAsync(default); await _box.ReadMirrorOwnerAsync(default);
        var item = new Dictionary<string, object?>
        {
            ["id"] = "codex",
            ["name"] = "Codex",
            ["targetId"] = new string('b', 64),
            ["accountContext"] = "",
            ["providerTargetId"] = "codex/"
        };
        switch (bad)
        {
            case "target-missing": item.Remove("targetId"); break;
            case "target-null": item["targetId"] = null; break;
            case "target-malformed": item["targetId"] = "private-malformed-canary"; break;
            case "account-missing": item.Remove("accountContext"); break;
            case "account-null": item["accountContext"] = null; break;
            case "account-wrong-type": item["accountContext"] = 7; break;
            case "provider-missing": item.Remove("providerTargetId"); break;
            case "provider-null": item["providerTargetId"] = null; break;
            case "provider-inconsistent": item["providerTargetId"] = "codex/other-private-canary"; break;
        }
        var result = bad == "list-wrong-type" ? "[]" : JsonSerializer.Serialize(new { agents = bad == "item-wrong-type" ? new object[] { "private-item-canary" } : [item] });
        _stub.Reply = (feature, method) => feature == "machines" && method == "agents" ? result : null;
        var error = await Assert.ThrowsAsync<CallFailed>(() => _box.AgentsAsync(default));
        Assert.Equal("incompatible backend", error.Message);
    }

    [Fact]
    public async Task Remote_agent_discovery_cannot_reuse_verified_identity_after_disconnect_and_replacement()
    {
        await _box.ProbeAsync(default); await _box.ReadMirrorOwnerAsync(default);
        Assert.Equal("claude/", (await _box.AgentsAsync(default))[0].ProviderTargetId);
        _stub.Drop = true;
        await Assert.ThrowsAsync<MachineUnreachable>(() => _box.AgentsAsync(default));
        _stub.Drop = false; _stub.EngineMachine = "replacement-engine";
        await _box.ProbeAsync(default);
        await Assert.ThrowsAsync<MachineUnreachable>(() => _box.AgentsAsync(default));
        Assert.Equal("replacement-engine", (await _box.ReadMirrorOwnerAsync(default)).Info.Machine);
        Assert.Equal("claude/", (await _box.AgentsAsync(default))[0].ProviderTargetId);
    }
}
