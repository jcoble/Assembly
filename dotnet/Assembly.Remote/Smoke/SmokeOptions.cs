using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;

namespace Assembly.Remote.Smoke;

/// <summary><c>smoke [--cwd DIR] [--log FILE] [--timeout SECONDS] -- &lt;adapter command…&gt;</c>. The timeout covers the whole run.</summary>
public sealed record SmokeOptions(AgentCommand Agent, string LogPath, TimeSpan RunTimeout)
{
    public const string Usage =
        "usage: Assembly.Engine smoke [--cwd DIR] [--log FILE] [--timeout SECONDS] -- <adapter command...>";

    public static SmokeOptions? Parse(IReadOnlyList<string> args)
    {
        string? cwd = null;
        var log = Path.Combine("logs", $"smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        var timeout = TimeSpan.FromSeconds(300);

        var i = 0;
        for (; i < args.Count && args[i] != "--"; i += 2)
        {
            if (i + 1 >= args.Count)
                return null;
            switch (args[i])
            {
                case "--cwd": cwd = args[i + 1]; break;
                case "--log": log = args[i + 1]; break;
                case "--timeout" when int.TryParse(args[i + 1], out var seconds): timeout = TimeSpan.FromSeconds(seconds); break;
                default: return null;
            }
        }

        var command = args.Skip(i + 1).ToList();
        if (command.Count == 0)
            return null;

        cwd = Path.GetFullPath(cwd ?? Directory.CreateTempSubdirectory("assembly-smoke-").FullName);
        return new SmokeOptions(new AgentCommand(command[0], command[1..], cwd), log, timeout);
    }
}
