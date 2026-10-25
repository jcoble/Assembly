using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Assembly.Engine.Features;
using Assembly.Engine.Preferences;

namespace Assembly.Tests.Draft.Base;

/// <summary>The preferences feature's calls, on a bare server on a free port.</summary>
public sealed class PreferencesRoutesTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync() => (_app, _http) = await StartAsync();

    private async Task<(WebApplication, HttpClient)> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        PreferencesRoutes.Add(app, new FeatureContext(_db.Path));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    // Pins: base#7
    [Fact]
    public async Task Saved_values_come_back_after_a_restart()
    {
        var set = await _http.PostAsJsonAsync("/preferences/set", new { values = new { theme = "dark", sessionsWidth = 280 } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        await _app.StopAsync();
        await _app.DisposeAsync();

        (_app, _http) = await StartAsync();
        var get = await _http.PostAsJsonAsync("/preferences/get", new { });
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var result = (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal("dark", result.GetProperty("theme").GetString());
        Assert.Equal(280, result.GetProperty("sessionsWidth").GetInt32());
    }

    // Pins: base#7
    [Fact]
    public async Task A_later_save_replaces_only_the_keys_it_names()
    {
        await _http.PostAsJsonAsync("/preferences/set", new { values = new { theme = "dark", sessionsWidth = 280 } });
        await _http.PostAsJsonAsync("/preferences/set", new { values = new { theme = "light" } });

        var result = (await (await _http.PostAsJsonAsync("/preferences/get", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal("light", result.GetProperty("theme").GetString());
        Assert.Equal(280, result.GetProperty("sessionsWidth").GetInt32());
    }

    [Fact]
    public async Task An_unknown_method_is_404_with_an_error()
    {
        var answer = await _http.PostAsJsonAsync("/preferences/nope", new { });
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Contains("nope", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_set_without_values_is_400_with_an_error()
    {
        var answer = await _http.PostAsJsonAsync("/preferences/set", new { theme = "dark" });
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("values", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _db.Dispose();
    }
}
