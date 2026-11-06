using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Features.Agents;

namespace Assembly.Tests.Features.Agents;

public sealed class SignInMethodsTests
{
    [Fact]
    public void Raw_methods_reject_malformed_and_non_login_choices()
    {
        var init = JsonSerializer.Deserialize("""
            {"protocolVersion":2,"authMethods":[{}, {"methodId":1}, "s",
             {"methodId":"key","name":"Key","type":"_api_key"},
             {"methodId":"gateway","name":"Gateway","type":"agent"},
             {"methodId":"api-key","name":"API key","type":"agent"},
             {"methodId":"login","name":"Login","type":"agent"}]}
            """, AcpJsonContext.Default.InitializeResponse)!;
        Assert.Equal("login", Assert.Single(SignInMethods.Runnable(init.AuthMethods)).MethodId);
    }

    [Fact]
    public void Private_metadata_cannot_turn_api_key_or_gateway_setup_into_login()
    {
        var init = JsonSerializer.Deserialize("""
            {"protocolVersion":2,"authMethods":[
             {"methodId":"first","name":"First","type":"agent","_meta":{"authType":"api-key"}},
             {"methodId":"second","name":"Second","type":"terminal","_meta":{"gateway":true}},
             {"methodId":"login","name":"Login","type":"agent"}]}
            """, AcpJsonContext.Default.InitializeResponse)!;
        Assert.Equal("login", Assert.Single(SignInMethods.Runnable(init.AuthMethods)).MethodId);
    }

    [Fact]
    public void Nested_known_setup_discriminators_are_excluded_without_filtering_arbitrary_metadata_text()
    {
        var init = JsonSerializer.Deserialize("""
            {"protocolVersion":2,"authMethods":[
             {"methodId":"one","name":"One","type":"agent","_meta":{"nested":{"mode":"gateway"}}},
             {"methodId":"two","name":"Two","type":"agent","_meta":{"nested":{"mode":"key"}}},
             {"methodId":"login","name":"Login","type":"agent","_meta":{"description":"gateway and api-key documentation"}}]}
            """, AcpJsonContext.Default.InitializeResponse)!;
        Assert.Equal("login", Assert.Single(SignInMethods.Runnable(init.AuthMethods)).MethodId);
    }

    [Fact]
    public void Non_device_method_is_chosen_before_device_and_present_strips_private_fields()
    {
        AuthMethod[] methods = [new("device-auth", "Device", "agent", null, [], [], null),
            new("callback", "Browser", "terminal", null, ["SECRET-ARGV"], [new("TOKEN", "SECRET-ENV")], null)];
        var chosen = SignInMethods.Choose(SignInMethods.Runnable(methods))!;
        Assert.Equal("callback", chosen.MethodId);
        var json = JsonSerializer.Serialize(SignInMethods.Present(chosen), ProvidersJson.Default.SignInChoice);
        Assert.Equal("{\"methodId\":\"callback\",\"name\":\"Browser\",\"kind\":\"terminal\"}", json);
        Assert.DoesNotContain("SECRET", json);
    }

    [Theory]
    [InlineData("http://localhost:1455/auth/callback", "localhost", 1455)]
    [InlineData("http://127.0.0.1:1455/auth/callback", "127.0.0.1", 1455)]
    [InlineData("http://[::1]:1455/auth/callback", "[::1]", 1455)]
    [InlineData("https://example.com/callback", null, 0)]
    public void Callback_only_accepts_loopback_redirects(string redirect, string? host, int port)
    {
        var link = new Uri("https://login.example/authorize?redirect_uri=" + Uri.EscapeDataString(redirect));
        var callback = SignInMethods.Callback(link);
        if (host is null) Assert.Null(callback);
        else Assert.Equal((host, port), callback);
    }
}
