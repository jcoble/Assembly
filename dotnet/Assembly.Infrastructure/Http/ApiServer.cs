using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Assembly.Infrastructure.Mcp;
using Assembly.Infrastructure.Socket;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// The API on 127.0.0.1 for the native desktop and remote machine. Port 0 picks a free port (for tests).
/// <paramref name="more"/> maps a host's own routes after the shared ones. <paramref name="dropSockets"/>
/// ends every open socket when requested.
/// </summary>
public static class ApiServer
{
    // Host shutdown is bounded to 8s, below the native launcher's 10s grace. Lifecycle tests exercise graceful exit,
    // escalation and reaping.
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    public static WebApplication Build(ApiServices services, int port, out Action dropSockets, Action<WebApplication>? more = null)
    {
        dropSockets = () => { };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        // Kestrel waits for open connections when it stops, so close subscriptions as stopping starts.
        app.Lifetime.ApplicationStopping.Register(services.Hub.CloseSubscriptions);
        app.UseWebSockets();
        app.Use(new TokenGuard(services.Token).InvokeAsync);
        new InfoRoutes(services.Machines).Map(app);
        new SessionRoutes(services.SessionStore, services.Machines).Map(app);
        new RowRoutes(services.Hub, services.SessionStore, services.Machines).Map(app);
        new RunRoutes(services.Hub, services.SessionStore, services.Machines, TimeProvider.System).Map(app);
        new OutboxRoutes(services.SessionStore).Map(app);
        new AttachmentRoutes(services.SessionStore, services.Machines, services.Attachments).Map(app);
        new ConfigRoutes(services.SessionStore, services.Machines).Map(app);
        if (services.Helpers is not null)
            new McpRoutes(services.Helpers, services.Board).Map(app);
        app.Map("/live", context =>
        {
            if (services.SessionStore.Get(context.Request.Query["session"].ToString()) is { } session)
                services.Machines.Named(session.Machine)?.Watch(session);
            return LiveSocket.HandleAsync(context, services.Hub);
        });
        if (services is { Writer: { } writer, Topics: { } topics })
        {
            var calls = new SocketDispatcher(writer, topics, TimeProvider.System, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Assembly.Calls"));
            foreach (var feature in services.Features ?? [])
                feature.Register(calls);
            var socket = new SocketEndpoint(services.Token, calls, Environment.MachineName, TimeProvider.System);
            var bulk = new SocketEndpoint(services.Token, calls, Environment.MachineName, TimeProvider.System, bulk: true);
            var terminal = new SocketEndpoint(services.Token, calls, Environment.MachineName, TimeProvider.System, terminal: true);
            app.Lifetime.ApplicationStopping.Register(terminal.CloseAll);
            app.Lifetime.ApplicationStopping.Register(socket.CloseAll);
            app.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
            dropSockets = () =>
            {
                socket.DropAll();
                bulk.DropAll();
                terminal.DropAll();
            };
            // Each is an exact route: /socket/other reaches neither.
            app.Map("/socket", socket.HandleAsync);
            app.Map("/socket/bulk", bulk.HandleAsync);
            app.Map("/socket/terminal", terminal.HandleAsync);
        }
        more?.Invoke(app);
        return app;
    }
}
