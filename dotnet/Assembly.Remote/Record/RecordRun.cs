using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Common.Acp;
using Assembly.Infrastructure.Transcript;

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
        using var transcript = TranscriptFile.Open(path);
        var agent = AgentConnection.Start(new AgentCommand(options.FileName, options.Arguments, cwd), new AllowOncePolicy(), transcript);
        Console.WriteLine($"{scenario.Name}: adapter pid {agent.ProcessId}; workspace {cwd}");
        string? sessionId = null;
        var updates = new UpdateWatcher(agent.Client.Updates);
        try
        {
            await agent.Client.InitializeAsync(new Implementation("assembly-backend", "0.1.0"), deadline.Token);
            var session = await agent.Client.NewSessionAsync(new(cwd, []), deadline.Token);
            sessionId = session.SessionId;
            return await scenario.RunAsync(new ScenarioContext(agent, session.SessionId, cwd, updates, deadline.Token, session.ConfigOptions, session.AvailableCommands));
        }
        finally
        {
            try
            {
                if (sessionId is not null && !agent.Exited.IsCompleted)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await agent.Client.CloseSessionAsync(sessionId, closing.Token);
                }
            }
            finally
            {
                await agent.DisposeAsync();
                await updates.Completion;
                Console.WriteLine($"{scenario.Name}: adapter pid {agent.ProcessId} exited");
            }
        }
    }
}
