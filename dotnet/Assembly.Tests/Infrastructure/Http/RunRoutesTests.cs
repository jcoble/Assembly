using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Draft.Base.Chat;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The run routes alone, on a bare server, a fake machine and a fake clock.</summary>
public sealed class RunRoutesTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private AppDatabase _db = null!;
    private LiveHub _hub = null!;
    private SessionStore _sessions = null!;
    private FakeMachine _here = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _db = TestDatabase.Open(_file.Path);
        _hub = new LiveHub(RowStore.Open(_db));
        _sessions = SessionStore.Open(_db, _time, "checkouts");
        _here = new FakeMachine(null, _sessions);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        new RunRoutes(_hub, _sessions, new MachineSet(_here), _time).Map(_app);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task A_send_cut_off_by_its_30_s_limit_answers_502_saying_so()
    {
        var session = _sessions.Create("claude", "repo");
        _here.Hold();

        var answer = _http.PostAsync($"/sessions/{session.SessionId}/send", new StringContent("""{"text":"hi"}""", Encoding.UTF8, "application/json"));
        await _here.Reached.Task.WaitAsync(Wait);
        _time.Advance(TimeSpan.FromSeconds(30));
        var reply = await answer.WaitAsync(Wait);

        Assert.Equal(HttpStatusCode.BadGateway, reply.StatusCode);
        using var body = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        Assert.Equal("send ran over 30 s", body.RootElement.GetProperty("error").GetString());
    }
}
