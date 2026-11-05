using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.DevSeed;

/// <param name="Steps">Each step's state by its key; a step not named stays pending.</param>
public sealed record SeedRun(string Key, string OwnerChat, string Project, string Definition, string Title, string State, double MinutesAgo,
    IReadOnlyDictionary<string, string> Steps, string? Reason = null, bool OpenGate = false);

/// <summary>
/// Runs started through the engine's own start, then moved to how they would have ended. No run is left with work the engine
/// could pick up: nothing is in its outbox and no step has a workspace, so opening the app starts no provider.
/// </summary>
public static class SeedWorkflows
{
    public static int Write(IWriteScope scope, SeedWorld world, string machine, IReadOnlyList<SeedRun> runs, IReadOnlyList<SeedChat> chats)
    {
        var commands = new EngineCommands(machine, new SessionLimits(() => 3, () => 8));
        var owners = chats.ToDictionary(chat => chat.Key, chat => chat.Id);
        foreach (var run in runs)
        {
            var at = world.Ago(run.MinutesAgo);
            var key = "run/" + run.Key;
            var id = RunId(run);
            var root = world.Project(run.Project);
            var project = new ProjectRecord(SeedDatabase.ProjectId(run.Project), SeedWorld.Machine, root, run.Project, null, null, null, at, null);
            var snapshot = WorkflowLoader.Load(root, run.Definition);
            var request = new WorkflowStartRequest(SeedWorld.GuidOf(key), id, 0, 0, owners[run.OwnerChat], key, run.Definition, project.Id, run.Title, null,
                JsonSerializer.SerializeToElement(new { }));
            var context = new CommandContext(request.CommandId, machine, new(ActorKind.Window, "owner"), id, 0, 0, at);
            var ack = WorkflowStarts.Apply(scope, commands, context, WorkflowStarts.Prepare(context, request, snapshot, project));
            if (!ack.Result.Applied) throw new InvalidDataException($"Seed run {run.Key} was refused: {ack.Result.Error}.");
            Settle(scope, world, id, run, at);
        }
        foreach (var run in runs.Where(run => run.OpenGate))
            scope.Execute("UPDATE engine_run SET paused=0 WHERE id=$id", new SqlArg("$id", RunId(run)));
        WorkflowGates.OpenReady(scope, machine, world.Ago(runs.Where(run => run.OpenGate).Min(run => run.MinutesAgo)));
        return runs.Count;
    }

    private static string RunId(SeedRun run) => SeedWorld.GuidOf("run/" + run.Key).ToString("N");

    private static void Settle(IWriteScope scope, SeedWorld world, string id, SeedRun run, long at)
    {
        var stepMs = 240_000L; var index = 0;
        foreach (var (key, state) in run.Steps)
        {
            var started = at + index++ * stepMs;
            var ended = state is "succeeded" or "failed" or "cancelled" or "skipped" ? started + stepMs : (long?)null;
            scope.Execute("""
                UPDATE engine_step SET state=$state,attempt=1,attempt_in_generation=1,started_ms=$started,ended_ms=$ended,outcome_reason=$reason,
                  dispatch_state=NULL,updated_ms=$started,version=version+1 WHERE run_id=$run AND definition_key=$key
                """, new SqlArg("$state", state), new("$started", started), new("$ended", ended), new("$reason", state == "failed" ? run.Reason : null), new("$run", id), new("$key", key));
        }
        var terminal = run.State is "succeeded" or "failed" or "cancelled";
        // Paused holds the run still until the gate pass below. A terminal run keeps no workspace to clean up.
        scope.Execute("""
            UPDATE engine_run SET state=$state,setup_state='ready',paused=1,started_ms=$started,ended_ms=$ended,cleanup_state=$cleanup,updated_ms=$updated,version=version+1 WHERE id=$run;
            UPDATE engine_run SET paused=0 WHERE id=$run AND $terminal=1;
            DELETE FROM engine_outbox WHERE run_id=$run;
            """, new SqlArg("$state", run.State), new("$started", at), new("$ended", terminal ? at + index * stepMs : null), new("$cleanup", terminal ? "removed" : "retained"),
            new("$updated", at + index * stepMs), new("$run", id), new("$terminal", terminal ? 1 : 0));
    }
}
