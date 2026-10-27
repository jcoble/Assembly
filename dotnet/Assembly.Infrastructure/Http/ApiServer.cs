using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Assembly.Infrastructure.Mcp;
using Assembly.Infrastructure.Socket;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// The API on 127.0.0.1: the webview's, on the desktop, and the desktop's, on a remote machine. Port 0 picks a free port
/// (for tests). <paramref name="more"/> maps a host's own routes after the shared ones.
/// </summary>
public static class ApiServer
{
    public static WebApplication Build(ApiServices services, int port, Action<WebApplication>? more = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
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
        if (services.Writer is not null)
        {
            var calls = new SocketDispatcher(services.Writer, TimeProvider.System, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Assembly.Calls"));
            var socket = new SocketEndpoint(services.Token, calls, Environment.MachineName, TimeProvider.System);
            app.Lifetime.ApplicationStopping.Register(socket.CloseAll);
            app.Map("/socket", socket.HandleAsync);
        }
        more?.Invoke(app);
        return app;
    }
}
