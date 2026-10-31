using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Runs;

namespace Assembly.Application.Features.Engine.Recording;

public static class RecordingTransitions
{
    private sealed record ToolRow(Attribution Attribution, long StartedMs, long? EndedMs, bool Late);

    public static CommandResult Apply(IWriteScope s, CommandContext c, RecordingCommand command)
    {
        if (c.Actor.Kind != ActorKind.System) return Refused(c, "forbidden");
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        if (command is ToolStarted invalidStart && (string.IsNullOrWhiteSpace(invalidStart.Boundary) || string.IsNullOrWhiteSpace(invalidStart.BoundaryId)) ||
            command is TurnUsage invalidUsage && string.IsNullOrWhiteSpace(invalidUsage.ProviderTurnId))
            return Refused(c, "bad_args");
        // End attribution is checked before receipt lookup so every mismatch is refused consistently.
        if (command is ToolEnded end)
        {
            var tool = FindTool(s, "id=$id", new SqlArg("$id", c.EntityId));
            if (tool is null) return Refused(c, "bad_args");
            if (tool.Attribution != end.Attribution) return Refused(c, "attribution_mismatch");
        }
        var key = command switch
        {
            ToolStarted start => Key("start", start.Boundary, start.BoundaryId),
            ToolEnded => Key("end", c.EntityId, ""),
            TurnUsage usage => Key("usage", usage.Attribution.SessionId, usage.ProviderTurnId),
            _ => throw new ArgumentException("Unknown recording command.", nameof(command))
        };
        // Logical boundary identity ignores the delivery's actor, entity alias, expected version and command id.
        // Both receipts remain in the caller's transaction; event retention cannot break boundary replay.
        var receiptContext = c with
        {
            CommandId = key,
            EntityId = "recording-boundary",
            ExpectedVersion = 0,
            Epoch = 0,
            Actor = new(ActorKind.System, "boundary-receipt")
        };
        var canonical = JournalJson(command);
        var result = EngineJournal.Execute(s, receiptContext, canonical, () => command switch
        {
            ToolStarted start => Start(s, c, start),
            ToolEnded finish => End(s, c, finish),
            TurnUsage usage => Usage(s, c, usage),
            _ => Refused(c, "bad_args")
        });
        // A refused delivery is journaled by the outer command receipt, but must not reserve a boundary id.
        if (!result.Applied && result.Error != "command_id_conflict")
            s.Execute("DELETE FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", key.ToString("N")));
        return result.Error == "command_id_conflict" ? Refused(c, "bad_args") : result;
    }

    private static CommandResult Start(IWriteScope s, CommandContext c, ToolStarted cmd)
    {
        if (cmd.InputBytes < 0 || string.IsNullOrWhiteSpace(cmd.Boundary) ||
            string.IsNullOrWhiteSpace(cmd.BoundaryId) || string.IsNullOrWhiteSpace(cmd.Tool))
            return Refused(c, "bad_args");
        var preview = Preview.Create(cmd.Input);
        if (FindTool(s, "id=$id", new SqlArg("$id", c.EntityId)) is not null) return Refused(c, "bad_args");
        var status = AttributionStatus(s, cmd.Attribution);
        if (!status.Valid) return Refused(c, "bad_args");
        var a = cmd.Attribution;
        s.Execute("""
            INSERT INTO engine_tool_call(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,attempt,epoch,
                boundary,boundary_id,tool,input_preview,input_bytes,started_ms,late)
            VALUES($id,1,$machine,$now,$now,$session,$run,$step,$attempt,$epoch,$boundary,$boundaryId,$tool,$input,$bytes,$now,$late)
            """, [.. Identity(c, a), new("$boundary", cmd.Boundary), new("$boundaryId", cmd.BoundaryId),
                new("$tool", cmd.Tool), new("$input", preview), new("$bytes", cmd.InputBytes), new("$late", status.Late ? 1 : 0)]);
        return Recorded(s, c, a, "tool_call", "tool.started", 1, status.Late);
    }

    private static CommandResult End(IWriteScope s, CommandContext c, ToolEnded cmd)
    {
        var tool = FindTool(s, "id=$id", new SqlArg("$id", c.EntityId));
        if (tool is null) return Refused(c, "bad_args");
        if (tool.Attribution != cmd.Attribution) return Refused(c, "attribution_mismatch");
        if (cmd.OutputBytes < 0 || string.IsNullOrWhiteSpace(cmd.Outcome)) return Refused(c, "bad_args");
        var preview = Preview.Create(cmd.Output);
        if (tool.EndedMs is not null) return Refused(c, "bad_args");
        // Wall clocks may move backwards. Keep the start immutable and clamp the end to a nonnegative duration.
        var ended = Math.Max(c.NowMs, tool.StartedMs);
        _ = checked(ended - tool.StartedMs);
        var late = tool.Late || AttributionStatus(s, tool.Attribution).Late;
        s.Execute("""
            UPDATE engine_tool_call SET version=2,updated_ms=$now,ended_ms=$ended,outcome=$outcome,
                output_preview=$output,output_bytes=$bytes,late=$late WHERE id=$id
            """, new("$id", c.EntityId), new("$now", c.NowMs), new("$ended", ended), new("$outcome", cmd.Outcome),
            new("$output", preview), new("$bytes", cmd.OutputBytes), new("$late", late ? 1 : 0));
        return Recorded(s, c, tool.Attribution, "tool_call", "tool.ended", 2, late);
    }

    private static CommandResult Usage(IWriteScope s, CommandContext c, TurnUsage cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.ProviderTurnId) || cmd.InputTokens < 0 || cmd.OutputTokens < 0 ||
            cmd.CachedTokens < 0 || cmd.CostNanodollars < 0 || cmd.DurationMs < 0 || cmd.CachedTokens > cmd.InputTokens)
            return Refused(c, "bad_args");
        if (s.QueryFirst("SELECT COUNT(*) FROM engine_usage WHERE id=$id", r => r.Long(0), new SqlArg("$id", c.EntityId)) != 0)
            return Refused(c, "bad_args");
        var status = AttributionStatus(s, cmd.Attribution);
        if (!status.Valid) return Refused(c, "bad_args");
        s.Execute("""
            INSERT INTO engine_usage(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,attempt,epoch,
                provider_turn_id,input_tokens,output_tokens,cached_tokens,cost_nanodollars,duration_ms,late)
            VALUES($id,1,$machine,$now,$now,$session,$run,$step,$attempt,$epoch,$turn,$input,$output,$cached,$cost,$duration,$late)
            """, [.. Identity(c, cmd.Attribution), new("$turn", cmd.ProviderTurnId), new("$input", cmd.InputTokens),
                new("$output", cmd.OutputTokens), new("$cached", cmd.CachedTokens), new("$cost", cmd.CostNanodollars),
                new("$duration", cmd.DurationMs), new("$late", status.Late ? 1 : 0)]);
        return Recorded(s, c, cmd.Attribution, "usage", "usage.recorded", 1, status.Late);
    }

    private static (bool Valid, bool Late) AttributionStatus(IWriteScope s, Attribution a)
    {
        if (string.IsNullOrWhiteSpace(a.SessionId) || a.Epoch < 0 || a.Attempt < 0 ||
            a.StepId is not null && (a.RunId is null || a.Attempt is null) || a.StepId is null && a.Attempt is not null)
            return (false, false);
        var session = s.QueryFirst("SELECT state FROM engine_session WHERE id=$id", r => r.Text(0), new SqlArg("$id", a.SessionId));
        if (session is null) return (false, false);
        var late = session == "ended";
        if (a.RunId is not null)
        {
            var run = s.QueryFirst("SELECT state FROM engine_run WHERE id=$id", r => r.Text(0), new SqlArg("$id", a.RunId));
            if (run is null) return (false, false);
            late |= RunTransitions.IsTerminal(run);
        }
        if (a.StepId is not null)
        {
            var step = s.QueryFirst("SELECT run_id,state FROM engine_step WHERE id=$id",
                r => new { Run = r.Text(0), State = r.Text(1) }, new SqlArg("$id", a.StepId));
            if (step is null || step.Run != a.RunId) return (false, false);
            late |= step.State is "succeeded" or "failed" or "skipped" or "cancelled";
        }
        return (true, late);
    }

    private static ToolRow? FindTool(IWriteScope s, string predicate, params SqlArg[] args) => s.QueryFirst($"""
        SELECT session_id,run_id,step_id,attempt,epoch,started_ms,ended_ms,late FROM engine_tool_call WHERE {predicate}
        """, r => new ToolRow(AttributionFrom(r, 0), r.Long(5), Number(r, 6), r.Long(7) != 0), args);

    private static Attribution AttributionFrom(IRow r, int i) => new(r.Text(i), Text(r, i + 1), Text(r, i + 2),
        r.IsNull(i + 3) ? null : checked((int)r.Long(i + 3)), r.Long(i + 4));
    private static string? Text(IRow r, int i) => r.IsNull(i) ? null : r.Text(i);
    private static long? Number(IRow r, int i) => r.IsNull(i) ? null : r.Long(i);

    private static SqlArg[] Identity(CommandContext c, Attribution a) =>
        [new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs), new("$session", a.SessionId),
            new("$run", a.RunId), new("$step", a.StepId), new("$attempt", a.Attempt), new("$epoch", a.Epoch)];

    private static CommandResult Recorded(IWriteScope s, CommandContext c, Attribution a, string entityKind,
        string kind, long version, bool late) => EngineJournal.Commit(s, c, version, a.Epoch,
        [new(late ? kind + ".late" : kind, entityKind, c.EntityId, version, late,
            new(a.SessionId, a.RunId, a.StepId, null, null, a.Epoch))], [], late);

    internal static string JournalJson(EngineCommand command)
    {
        var canonical = JsonSerializer.Serialize(ForJournal(command), EngineJson.Default.EngineCommand);
        var argument = command switch
        {
            ToolStarted start => start.Input,
            ToolEnded end => end.Output,
            _ => default
        };
        // Compare full transient arguments without storing their values, including equal-length scalar changes.
        // Only the journal's final SHA-256 fingerprint is persisted, never this text or the argument digest.
        return argument.ValueKind == JsonValueKind.Undefined ? canonical
            : canonical + "\n" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(argument.GetRawText())));
    }

    private static EngineCommand ForJournal(EngineCommand command) => command switch
    {
        ToolStarted start => start with { Input = JsonSerializer.SerializeToElement(Preview.Create(start.Input), EngineJson.Default.String) },
        ToolEnded end => end with { Output = JsonSerializer.SerializeToElement(Preview.Create(end.Output), EngineJson.Default.String) },
        _ => command
    };

    private static Guid Key(string kind, string first, string second)
    {
        // Length prefixes avoid delimiter ambiguity; the domain prefix separates internal and delivery receipts.
        var identity = $"engine-recording/{kind}/{first.Length}:{first}{second.Length}:{second}";
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }

    private static CommandResult Refused(CommandContext c, string error) => new(false, false, error, c.ExpectedVersion, 0, c.Epoch);
}
