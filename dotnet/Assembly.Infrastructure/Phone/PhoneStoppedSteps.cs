using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Workflows.Runs;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Http;
using Microsoft.AspNetCore.Http;

namespace Assembly.Infrastructure.Phone;

/// <summary>A step the engine says is stopped until the person moves it, as an inbox item, so a phone counts what a window counts.</summary>
internal static class PhoneStoppedSteps
{
    internal const string Prefix = "step:";
    private static readonly PhoneNeedsRoutes.Option[] Retry = [new("retry", "Retry", "primary")];

    internal static IReadOnlyList<PhoneNeedsRoutes.Item> Read(IReadScope scope, string machine, string? stepId = null) => scope.Query($"""
        SELECT t.id,CAST(s.title AS TEXT),t.run_id,COALESCE(t.definition_key,t.name),t.state,t.outcome_reason,
            t.attempt_in_generation,t.max_attempts,t.updated_ms,r.version,s.epoch
        FROM engine_run r JOIN engine_step t ON t.run_id=r.id JOIN engine_session s ON s.id=r.owner_session_id
        WHERE r.state IN ('queued','running','needs_me') AND r.machine=$machine AND ($step IS NULL OR t.id=$step)
            AND s.machine=$machine AND s.holder='local' AND s.reserved_epoch IS NULL
            AND s.state NOT IN ('foreign','handing_off','ended') AND {OwnerAttention.Step}
        ORDER BY t.updated_ms DESC,t.id
        """, row =>
    {
        var (name, state, reason) = (row.Text(3), row.Text(4), row.IsNull(5) ? null : row.Text(5));
        var retryable = state == "failed" || reason != "scope_required";
        var (title, detail) = (state, reason) switch
        {
            ("failed", _) => ($"{name} failed", $"Attempt {row.Long(6)} of {row.Long(7)} failed. Retry runs it again."),
            (_, "timed_out") => ($"{name} timed out", "Retry runs it again."),
            (_, "scope_required") => ($"{name} is ready to continue", "Continue it from a desktop."),
            _ => ($"{name} was interrupted", "Retry runs it again."),
        };
        return new PhoneNeedsRoutes.Item(Prefix + row.Text(0), "restart", row.Text(1), row.Text(2), name, title, detail, null,
            row.Long(8), row.Long(9), row.Long(10), false, retryable ? Retry : []);
    }, new SqlArg("$machine", machine), new("$step", stepId));

    internal static async Task RetryAsync(HttpContext context, EngineRuntime runtime, PhonePeer peer, string id, string option, long version,
        Action<IWriteScope, PhonePeer> ensureCurrentPeer)
    {
        var step = id[Prefix.Length..];
        var item = ((IEngineRuntime)runtime).Read(scope => Read(scope, runtime.Machine, step).SingleOrDefault(), context.RequestAborted);
        if (item is null || item.Version != version)
        { await StaleAsync(context); return; }
        if (!item.Options.Any(candidate => candidate.Id == option))
        { await JsonReply.ErrorAsync(context, 400, "That option is not offered for this item"); return; }
        var command = new CommandContext(Guid.NewGuid(), runtime.Machine, new(ActorKind.Window, "owner"), item.RunId!, version, item.Epoch,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new("phone", peer.DeviceId, null));
        var result = ((IEngineRuntime)runtime).Write(scope =>
        {
            ensureCurrentPeer(scope, peer);
            return runtime.Commands.Apply(scope, command, new RetryWorkflowStep(step));
        });
        if (result.Applied) context.Response.StatusCode = 204;
        else if (result.Error is "version_conflict" or "stale_epoch" or "invalid_transition") await StaleAsync(context);
        else await JsonReply.ErrorAsync(context, 400, result.Error ?? "retry refused");
    }

    private static Task StaleAsync(HttpContext context) => JsonReply.WriteAsync(context, 409, json =>
    {
        json.WriteStartObject();
        json.WriteString("error", "stale");
        json.WriteEndObject();
    });
}
