using Assembly.Application.Common.Acp.JsonRpc;

namespace Assembly.Remote.Record;

/// <summary>
/// The conversations step 2's converter is tested against. Between them they produce text with markdown,
/// thinking, Read, Bash, an edit that asks permission, a plan, a failing tool, a JSON-RPC error,
/// a cancelled turn, a lost run, and a
/// command left running in the background. Each returns a one-line summary for the console.
/// </summary>
public static class Scenarios
{
    private static readonly IReadOnlyDictionary<string, string> NoFiles = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, string> Notes = new Dictionary<string, string> { ["notes.txt"] = "apple\nbanana\ncherry\n" };

    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("text", NoFiles, async c =>
        {
            // High effort makes Claude think first, so the fixture has thinking as well as text.
            await c.Agent.Client.SetConfigOptionAsync(c.SessionId, "effort", "high", c.Deadline);
            return await PromptAsync(c,
                "Explain what a mutex is in two short paragraphs. Include one small C# code block, " +
                "a markdown table with two columns and three rows, and a bulleted list with a nested item. Don't use any tools.");
        }),
        new("tools", new Dictionary<string, string> { ["notes.txt"] = "apple\nbanana\ncherry\n" }, c =>
            PromptAsync(c,
                "First make a todo list with three items for this task using your todo tool. Then read notes.txt, " +
                "run `wc -l notes.txt` in the shell, and change the word apple to pear in notes.txt. " +
                "Update the todo list as you finish each item. End with one short sentence.")),
        new("tool-fails", NoFiles, c =>
            PromptAsync(c, "Run exactly this shell command and tell me the error in one sentence: ls /definitely-not-here-7731")),
        new("error", NoFiles, async c =>
        {
            try
            {
                await c.Agent.Client.PromptAsync("no-such-session", "hello", c.Deadline);
                return "unexpected: the prompt succeeded";
            }
            catch (JsonRpcException ex)
            {
                return $"error {ex.Code}: {ex.Message}";
            }
        }),
        new("cancel", NoFiles, async c =>
        {
            var turn = c.Updates.BeginTurn();
            var prompt = c.Agent.Client.PromptAsync(c.SessionId, "Write the numbers from 1 to 300, one per line, with no other text.", c.Deadline);
            await Task.WhenAny(turn.FirstText, Task.Delay(TimeSpan.FromSeconds(30)));
            await c.Agent.Client.CancelAsync(c.SessionId, c.Deadline);
            return $"stop {(await prompt).StopReason}";
        }),
        new("lost", NoFiles, async c =>
        {
            var turn = c.Updates.BeginTurn();
            var prompt = c.Agent.Client.PromptAsync(c.SessionId, "Write the numbers from 1 to 300, one per line, with no other text.", c.Deadline);
            await Task.WhenAny(turn.FirstText, Task.Delay(TimeSpan.FromSeconds(30)));
            c.Agent.Kill();
            await c.Agent.Exited;
            try
            {
                await prompt;
                return "unexpected: the prompt finished";
            }
            catch (IOException)
            {
                return "adapter killed mid-turn; the prompt never answered";
            }
        }),
        new("background", NoFiles, BackgroundAsync),
        new("background-async", NoFiles, BackgroundAsync, ["asyncTasks"]),
        // A message steered in while a foreground command runs: Claude Code moves the command to the background.
        new("steer-background", NoFiles, SteerBackgroundAsync),
        // The agent's slash commands (available_commands_update), then one of them sent as a prompt.
        new("slash-context", NoFiles, c => SlashAsync(c, "/context")),
        // Codex has no /context; /status is its nearest.
        new("slash-status", NoFiles, c => SlashAsync(c, "/status")),
        // A subagent, announced as a child session (clientCapabilities.subagents).
        new("subagent", Notes, SubagentAsync),
        // The same with the AIR opt-ins the window gives Codex (agents-file.sh).
        new("subagent-air", Notes, SubagentAsync, ["sessionIndex", "asyncTasks"]),
    ];

    private static Task<string> SubagentAsync(ScenarioContext c) => PromptAsync(c,
        "Use a subagent (your Agent or Task tool, or spawn an agent) to read notes.txt and report how many lines it has. " +
        "Keep it short. Then reply with just the number.");

    private static async Task<string> SlashAsync(ScenarioContext c, string command)
    {
        // The list comes as an update just after session/new.
        await Task.Delay(TimeSpan.FromSeconds(3), c.Deadline);
        return await PromptAsync(c, command);
    }

    // A command left running after the turn ends, and whatever the agent does when it finishes: with the AIR
    // asyncTasks opt-in (background-async) and without it (background).
    private static async Task<string> BackgroundAsync(ScenarioContext c)
    {
        var summary = await PromptAsync(c,
            "Start this shell command in the background, without waiting for it: sleep 15 && echo finished. " +
            "Reply with just: started. When it finishes, tell me in one line what it printed.");
        var after = c.Updates.BeginTurn();
        await Task.Delay(TimeSpan.FromSeconds(60), c.Deadline);
        return $"{summary}; after the turn: {after.KindSummary}";
    }

    private static async Task<string> SteerBackgroundAsync(ScenarioContext c)
    {
        var turn = c.Updates.BeginTurn();
        var prompt = c.Agent.Client.PromptAsync(c.SessionId,
            "Run this in the foreground with your Bash tool (not in the background), with a 120000 ms timeout: " +
            "for i in $(seq 1 30); do echo step $i; sleep 1; done. Then tell me the last line it printed.", c.Deadline);
        // Late enough that the loop is running (the agent starts it some seconds in).
        await Task.Delay(TimeSpan.FromSeconds(14), c.Deadline);
        var steered = await c.Agent.Client.SteerAsync(c.SessionId, "Also, once the loop is done, say how many files are in this folder.", c.Deadline);
        var response = await prompt;
        await c.Updates.DrainAsync();
        var after = c.Updates.BeginTurn();
        await Task.Delay(TimeSpan.FromSeconds(60), c.Deadline);
        return $"steer {steered.Outcome}; stop {response.StopReason}; updates: {turn.KindSummary}; after the turn: {after.KindSummary}";
    }

    private static async Task<string> PromptAsync(ScenarioContext c, string text)
    {
        var turn = c.Updates.BeginTurn();
        var response = await c.Agent.Client.PromptAsync(c.SessionId, text, c.Deadline);
        await c.Updates.DrainAsync();
        return $"stop {response.StopReason}; updates: {turn.KindSummary}";
    }
}
