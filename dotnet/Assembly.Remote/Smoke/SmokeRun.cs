using System.Diagnostics;
using Assembly.Engine.Acp.Messages;
using Assembly.Engine.Agents;
using Assembly.Engine.Transcript;

namespace Assembly.Remote.Smoke;

/// <summary>
/// Step 1: drive a real adapter through initialize, session/new, a turn that needs permission,
/// and a turn we cancel. Every JSON-RPC line goes to the transcript; checks print at the end.
/// </summary>
public sealed class SmokeRun(SmokeOptions options) : IDisposable
{
    private const string FileName = "step1-permission-check.txt";

    private readonly List<SmokeCheck> _checks = [];
    private readonly AllowOncePolicy _permissions = new();
    private readonly CancellationTokenSource _deadline = new(options.RunTimeout);

    public static async Task<int> MainAsync(IReadOnlyList<string> args)
    {
        if (SmokeOptions.Parse(args) is not { } options)
        {
            Console.Error.WriteLine(SmokeOptions.Usage);
            return 2;
        }
        using var run = new SmokeRun(options);
        return await run.RunAsync() ? 0 : 1;
    }

    public async Task<bool> RunAsync()
    {
        Console.WriteLine($"agent: {options.Agent.FileName} {string.Join(' ', options.Agent.Arguments)}");
        Console.WriteLine($"cwd:   {options.Agent.WorkingDirectory}");
        Console.WriteLine($"log:   {Path.GetFullPath(options.LogPath)}");

        using var transcript = TranscriptLog.ToFile(options.LogPath);
        var agent = AgentConnection.Start(options.Agent, _permissions, transcript);
        Console.WriteLine($"pid:   {agent.ProcessId}");
        var watcher = new UpdateWatcher(agent.Client.Updates);
        try
        {
            await DriveAsync(agent, watcher);
        }
        catch (Exception ex)
        {
            Record("run", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            var stopping = Stopwatch.StartNew();
            await agent.DisposeAsync();
            Record("shutdown", true, $"adapter and its children ended in {stopping.ElapsedMilliseconds} ms");
        }

        Console.WriteLine();
        foreach (var check in _checks)
            Console.WriteLine(check);
        return _checks.All(c => c.Passed);
    }

    private async Task DriveAsync(AgentConnection agent, UpdateWatcher watcher)
    {
        var client = agent.Client;

        var init = await Timed(() => client.InitializeAsync(new Implementation("assembly-backend", "0.1.0"), null, Deadline()));
        Record("initialize", init.Value.ProtocolVersion == 1,
            $"protocol v{init.Value.ProtocolVersion}, agent {init.Value.AgentInfo?.Name} {init.Value.AgentInfo?.Version}, {init.Ms} ms");

        var session = await Timed(() => client.NewSessionAsync(options.Agent.WorkingDirectory, Deadline()));
        Record("session/new", session.Value.SessionId.Length > 0, $"session {session.Value.SessionId}, {session.Ms} ms");
        var sessionId = session.Value.SessionId;

        var toolTurn = watcher.BeginTurn();
        // Writing a file needs permission in the default mode; a read-only command like `echo` doesn't.
        var toolPrompt = $"Create a file named {FileName} in the current directory containing the single word ok. Then reply with only the word done.";
        var tool = await Timed(() => client.PromptAsync(sessionId, toolPrompt, Deadline()));
        await watcher.DrainAsync();
        Record("prompt (tool turn)", tool.Value.StopReason == "end_turn",
            $"stop {tool.Value.StopReason}, {tool.Ms} ms; updates: {toolTurn.KindSummary}");
        var written = Path.Combine(options.Agent.WorkingDirectory, FileName);
        Record("file written", File.Exists(written) && File.ReadAllText(written).Trim() == "ok",
            $"{written} {(File.Exists(written) ? "exists" : "is missing")}; agent said \"{Shorten(toolTurn.Text)}\"");
        Record("request_permission", _permissions.Asked.Count > 0,
            _permissions.Asked.Count > 0
                ? $"answered allow_once for: {string.Join("; ", _permissions.Asked)}"
                : "the agent never asked (its permission mode may allow writes without asking)");

        var cancelTurn = watcher.BeginTurn();
        var started = Stopwatch.StartNew();
        var prompt = client.PromptAsync(sessionId, "Write the numbers from 1 to 300, one per line, with no other text.", Deadline());
        await Task.WhenAny(cancelTurn.FirstText, Task.Delay(TimeSpan.FromSeconds(30)));
        var cancelAt = started.ElapsedMilliseconds;
        await client.CancelAsync(sessionId, Deadline());
        var cancelled = await prompt;
        var stoppedAt = started.ElapsedMilliseconds;
        await watcher.DrainAsync();
        Record("session/cancel", cancelled.StopReason == "cancelled",
            $"cancel sent at {cancelAt} ms, prompt answered {cancelled.StopReason} at {stoppedAt} ms; " +
            $"{cancelTurn.Text.Length} chars streamed; updates: {cancelTurn.KindSummary}");
    }

    private CancellationToken Deadline() => _deadline.Token;

    private void Record(string name, bool passed, string detail) => _checks.Add(new SmokeCheck(name, passed, detail));

    private static async Task<(T Value, long Ms)> Timed<T>(Func<Task<T>> call)
    {
        var clock = Stopwatch.StartNew();
        var value = await call();
        return (value, clock.ElapsedMilliseconds);
    }

    private static string Shorten(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    public void Dispose() => _deadline.Dispose();
}
