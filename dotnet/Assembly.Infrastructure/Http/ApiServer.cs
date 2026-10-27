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
/// The API on 127.0.0.1: the webview's, on the desktop, and the desktop's, on a remote machine. Port 0 picks a free port
/// (for tests). <paramref name="more"/> maps a host's own routes after the shared ones. <paramref name="dropSockets"/>
/// ends every open <c>/socket</c>, for the debug control's drop.
/// </summary>
public static class ApiServer
{
    // How long stopping may take: it drains each socket's queued saves (plan 5.9). Below the shell's wait after SIGTERM,
    // BACKEND_STOP_WAIT in shell/src/main.rs, so the backend finishes before the shell kills it
    // (scripts/shutdown-timing.test.sh checks it).
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    public static WebApplication Build(ApiServices services, int port, out Action dropSockets, Action<WebApplication>? more = null)
    {
        dropSockets = () => { };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        // Kestrel waits for open connections when it stops, and the webview's live sockets never close on their
        // own: close them as stopping starts.
        app.Lifetime.ApplicationStopping.Register(services.Hub.CloseSubscriptions);
        app.UseWebSockets();
        app.Use(new TokenGuard(services.Token).InvokeAsync);
        new InfoRoutes(services.Machines).Map(app);
        new SessionRoutes(services.SessionStore, services.Machines).Map(app);
        new RowRoutes(services.Hub, services.SessionStore, services.Machines).Map(app);
        new RunRoutes(services.Hub, services.SessionStore, services.Machines).Map(app);
        new OutboxRoutes(services.SessionStore).Map(app);
        new AttachmentRoutes(services.SessionStore, services.Machines, services.Attachments).Map(app);
        new ConfigRoutes(services.SessionStore, services.Machines).Map(app);
        if (services.Helpers is not null)
            new McpRoutes(services.Helpers).Map(app);
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
            app.Lifetime.ApplicationStopping.Register(socket.CloseAll);
            dropSockets = socket.DropAll;
            app.Map("/socket", socket.HandleAsync);
        }
        more?.Invoke(app);
        return app;
    }
}
