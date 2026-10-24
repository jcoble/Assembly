using Assembly.Engine.Acp.Messages;
using Assembly.Engine.Agents;
using Assembly.Remote.Smoke;
using Assembly.Engine.Transcript;

namespace Assembly.Remote.Record;

/// <summary>Records each scenario's full JSON-RPC transcript to <c>&lt;out&gt;/&lt;name&gt;.jsonl</c>, for step 2's fixtures.</summary>
public sealed class RecordRun(RecordOptions options)
{
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromMinutes(5);

    public static async Task<int> MainAsync(IReadOnlyList<string> args)
    {
        if (RecordOptions.Parse(args) is not { } options)
        {
            Console.Error.WriteLine(RecordOptions.Usage);
            return 2;
        }
        return await new RecordRun(options).RunAsync() ? 0 : 1;
    }

    public async Task<bool> RunAsync()
    {
        Directory.CreateDirectory(options.OutDirectory);
        var allPassed = true;
        foreach (var scenario in Scenarios.All.Where(s => options.Only.Count == 0 || options.Only.Contains(s.Name)))
        {
            var path = Path.Combine(options.OutDirectory, $"{scenario.Name}.jsonl");
            try
            {
                Console.WriteLine($"{scenario.Name}: {await RecordAsync(scenario, path)}");
            }
            catch (Exception ex)
            {
                allPassed = false;
                Console.WriteLine($"{scenario.Name}: FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
        return allPassed;
    }

    private async Task<string> RecordAsync(Scenario scenario, string path)
    {
        var cwd = Directory.CreateTempSubdirectory($"assembly-record-{scenario.Name}-").FullName;
        foreach (var (name, content) in scenario.Files)
            await File.WriteAllTextAsync(Path.Combine(cwd, name), content);

        using var deadline = new CancellationTokenSource(ScenarioTimeout);
        using var transcript = TranscriptLog.ToFile(path);
        var agent = AgentConnection.Start(new AgentCommand(options.FileName, options.Arguments, cwd), new AllowOncePolicy(), transcript);
        try
        {
            var updates = new UpdateWatcher(agent.Client.Updates);
            await agent.Client.InitializeAsync(new Implementation("assembly-backend", "0.1.0"), scenario.Air, deadline.Token);
            var session = await agent.Client.NewSessionAsync(cwd, deadline.Token);
            return await scenario.RunAsync(new ScenarioContext(agent, session.SessionId, cwd, updates, deadline.Token));
        }
        finally
        {
            await agent.DisposeAsync();
        }
    }
}
