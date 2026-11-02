using System.Text.Json;
using Assembly.Application.Common.Acp;

namespace Assembly.Tests.Acp;

public sealed partial class AcpAuthTests
{
    [Fact]
    public async Task Unspecified_terminal_execution_support_does_not_advertise_terminal_authentication()
    {
        await using var h = new Harness();
        var initializing = h.Client.InitializeAsync(new("fixture", "1"), default);
        var request = await h.Agent.ReadAsync();
        var capabilities = request.GetProperty("params").GetProperty("capabilities");
        var auth = capabilities.GetProperty("auth");
        Assert.False(auth.TryGetProperty("terminal", out _));
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("elicitation").GetProperty("form").ValueKind);
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("elicitation").GetProperty("url").ValueKind);
        await h.Agent.ReplyAsync(request, "{\"protocolVersion\":2,\"authMethods\":[]}");
        await initializing.WaitAsync(Limit);
        foreach (var supported in new[] { false, true })
        {
            await using var explicitOwner = new Harness();
            var explicitInitialize = explicitOwner.Client.InitializeAsync(new("fixture", "1"), supported, default);
            var explicitRequest = await explicitOwner.Agent.ReadAsync();
            var explicitAuth = explicitRequest.GetProperty("params").GetProperty("capabilities").GetProperty("auth");
            Assert.Equal(supported, explicitAuth.TryGetProperty("terminal", out var terminal));
            if (supported)
            {
                Assert.Equal(JsonValueKind.Object, terminal.ValueKind);
                Assert.Empty(terminal.EnumerateObject());
            }
            await explicitOwner.Agent.ReplyAsync(explicitRequest, """{"protocolVersion":2,"authMethods":[]}""");
            await explicitInitialize.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Duplicate_terminal_environment_names_refuse_only_that_descriptor()
    {
        await using var h = new Harness();
        using var methods = JsonDocument.Parse("""
            [{"methodId":"agent","name":"Agent","type":"agent"},
             {"methodId":"ambiguous","name":"Ambiguous","type":"terminal","env":[{"name":"LOGIN","value":"one"},{"name":"LOGIN","value":"two"}]},
             {"methodId":"configured","name":"Terminal","type":"terminal","command":"/unrelated-program","args":["--login"],"env":[{"name":"LOGIN","value":"1"}]}]
            """);
        var response = await h.Initialize(JsonSerializer.Serialize(methods.RootElement));
        Assert.Equal(["agent", "configured"], response.AuthMethods.Select(method => method.MethodId));
        var terminal = response.AuthMethods[1];
        Assert.Equal(["--login"], terminal.Args);
        Assert.Equal("LOGIN", Assert.Single(terminal.Env!).Name);
        Assert.DoesNotContain("command", JsonSerializer.Serialize(terminal, AcpJsonContext.Default.AuthMethod));
    }
}
