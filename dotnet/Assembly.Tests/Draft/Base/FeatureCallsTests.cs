using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Assembly.Infrastructure.Http;

namespace Assembly.Tests.Draft.Base;

/// <summary>The call shape every feature uses (FeatureCalls), on a bare server on a free port.</summary>
public sealed class FeatureCallsTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        // A call whose work waits on the request's token, like a store reading the database.
        new FeatureCalls(_app, "slow").Map("read", async (body, cancellationToken) =>
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancelled.TrySetResult();
                throw;
            }
            return json => json.WriteNullValue();
        });
        new FeatureCalls(_app, "broken").Map("read", (body, cancellationToken) => throw new InvalidOperationException("the disk is gone"));
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    // Pins: base#10
    [Fact]
    public async Task A_call_the_page_aborts_stops_its_work_in_the_backend()
    {
        using var abort = new CancellationTokenSource();
        var request = _http.PostAsync("/slow/read", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), abort.Token);
        await _started.Task.WaitAsync(Wait);

        await abort.CancelAsync();

        await _cancelled.Task.WaitAsync(Wait);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task A_call_that_throws_answers_500_with_an_error()
    {
        var answer = await _http.PostAsync("/broken/read", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, answer.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        Assert.Equal("broken/read failed: the disk is gone", body.RootElement.GetProperty("error").GetString());
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }
}
