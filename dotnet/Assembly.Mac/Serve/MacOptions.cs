using System.Globalization;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;

namespace Assembly.Mac.Serve;

/// <summary>
/// <c>serve [--port N] [--home DIR] [--token-file FILE] [--agents FILE] [--remote SSH-HOST] [--remote-port N]
/// [--remote-token-file PATH]</c>. The home folder holds the database, the sessions' checkouts and their attachments;
/// the agents file (<c>HOME/agents.json</c> unless given) lists the agents this Mac runs (<see cref="AgentDescriptor"/>).
/// <c>--remote</c> adds a machine running <c>Assembly.Remote serve</c> on its <c>--remote-port</c>, reached over ssh,
/// with its token in <c>--remote-token-file</c> (relative to its home folder).
/// </summary>
public sealed record MacOptions(int Port, string Home, string TokenFile, string AgentsFile, string? Remote = null, int RemotePort = 9797,
    string RemoteTokenFile = ".config/assembly-dotnet/env")
{
    public const string Usage =
        "usage: Assembly.Mac serve [--port 18797] [--home ~/assembly-dotnet/mac] [--token-file ~/.config/assembly-dotnet/env] [--agents HOME/agents.json] [--remote workbox-test] [--remote-port 9797] [--remote-token-file .config/assembly-dotnet/env]";

    public string Checkouts => Path.Combine(Home, "sessions");

    public static MacOptions? Parse(IReadOnlyList<string> args)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var port = 18797;
        var home = Path.Combine(userHome, "assembly-dotnet", "mac");
        var tokenFile = Path.Combine(userHome, ".config", "assembly-dotnet", "env");
        string? remote = null;
        string? agents = null;
        var remotePort = 9797;
        var remoteTokenFile = ".config/assembly-dotnet/env";

        var i = 0;
        for (; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count)
                return null;
            switch (args[i])
            {
                case "--port" when int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value): port = value; break;
                case "--home": home = args[i + 1]; break;
                case "--token-file": tokenFile = args[i + 1]; break;
                case "--remote": remote = args[i + 1]; break;
                case "--agents": agents = args[i + 1]; break;
                case "--remote-port" when int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value): remotePort = value; break;
                case "--remote-token-file": remoteTokenFile = args[i + 1]; break;
                default: return null;
            }
        }

        return new MacOptions(port, Path.GetFullPath(home), Path.GetFullPath(tokenFile), Path.GetFullPath(agents ?? Path.Combine(home, "agents.json")), remote,
            remotePort, remoteTokenFile);
    }
}
