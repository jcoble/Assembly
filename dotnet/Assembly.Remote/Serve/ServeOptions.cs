using System.Globalization;
using Assembly.Engine.Agents;

namespace Assembly.Remote.Serve;

/// <summary>
/// <c>serve [--port N] [--home DIR] [--token-file FILE] [--agents FILE]</c>. The home folder holds the database, the
/// threads' checkouts and their attachments; the agents file (<c>HOME/agents.json</c> unless given) lists the agents
/// this machine runs (<see cref="AgentDescriptor"/>).
/// </summary>
public sealed record ServeOptions(int Port, string Home, string TokenFile, string AgentsFile)
{
    public const string Usage =
        "usage: Assembly.Engine serve [--port 9797] [--home ~/assembly-dotnet/serve] [--token-file ~/.config/assembly-dotnet/env] [--agents HOME/agents.json]";

    public string Database => Path.Combine(Home, "assembly.db");

    public string Checkouts => Path.Combine(Home, "threads");

    public string Attachments => Path.Combine(Home, "attachments");

    public static ServeOptions? Parse(IReadOnlyList<string> args)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var port = 9797;
        var home = Path.Combine(userHome, "assembly-dotnet", "serve");
        var tokenFile = Path.Combine(userHome, ".config", "assembly-dotnet", "env");
        string? agents = null;

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
                case "--agents": agents = args[i + 1]; break;
                default: return null;
            }
        }

        return new ServeOptions(port, Path.GetFullPath(home), Path.GetFullPath(tokenFile), Path.GetFullPath(agents ?? Path.Combine(home, "agents.json")));
    }
}
