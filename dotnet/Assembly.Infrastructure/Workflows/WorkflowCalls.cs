using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Workflows;
using Assembly.Application.Features.Workflows.Contracts;

namespace Assembly.Infrastructure.Workflows;

/// <summary>Owner calls select exactly one source; page reads always use the desktop's SQL.</summary>
public sealed class WorkflowCalls(WorkflowQueries queries, Func<BoardAnswerArgs, CancellationToken, Task<BoardAnswerResult>> answer, RemoteWorkflowMirror? mirror = null) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Topic("board", WorkflowJson.Default.BoardChange,
            x => JsonSerializer.Serialize(new[] { x.MachineId, x.TaskKey }, WorkflowJson.Default.StringArray),
            (key, _) => key is null ? [] : throw new CallFailed("invalid_argument"));
        calls.Read("board", "page", WorkflowJson.Default.BoardPageArgs, WorkflowJson.Default.BoardPageResult,
            (args, context) => Task.FromResult(queries.Page(args, context.Cancellation)));
        calls.Read("board", "changes", WorkflowJson.Default.BoardChangesArgs, WorkflowJson.Default.BoardChangesResult,
            (args, context) => Task.FromResult(queries.Changes(args, context.Cancellation)));
        calls.Read("board", "task", WorkflowJson.Default.BoardTaskArgs, WorkflowJson.Default.BoardTaskResult,
            (args, context) => args.MachineId == "local"
                ? Task.FromResult(queries.Task(args, context.Cancellation))
                : mirror is not null && args.MachineId == mirror.Name ? mirror.TaskAsync(args, context.Cancellation)
                : throw new MachineUnreachable(args.MachineId, "Unknown Board source"));
        calls.Write("board", "answer", WorkflowJson.Default.BoardAnswerArgs, WorkflowJson.Default.BoardAnswerResult,
            async (args, context) =>
            {
                var result = args.MachineId == "local" ? await answer(args, context.Cancellation) :
                    mirror is not null && args.MachineId == mirror.Name ? await mirror.AnswerAsync(args, context.Cancellation) :
                    throw new MachineUnreachable(args.MachineId, "Unknown Board source");
                return _ => result;
            }, args => JsonSerializer.Serialize(new[] { args.MachineId, args.TaskKey, args.QuestionKey }, WorkflowJson.Default.StringArray));
    }
}
