using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Engine;

namespace Assembly.Infrastructure.Engine.Workflows;

public sealed record WorkflowRunsArgs(string? Project = null, string? State = null, string? After = null, int Limit = 50, string? Machine = null);
public sealed record WorkflowRunArgs(string RunId, string? After = null, string? Machine = null);
public sealed record WorkflowHistoryArgs(string RunId, string? After = null, int Limit = 50, string? Machine = null);
public sealed record WorkflowReceiptArgs(string ReceiptId, string? After = null, int Limit = 50, string? Machine = null);

/// <summary>Explicit owner reads of one Engine snapshot, locally or on the selected owning machine.</summary>
internal sealed class WorkflowCalls(EngineRuntime runtime, MachineForward? forward = null) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Read("workflow", "runs", WorkflowReadCallsJson.Default.WorkflowRunsArgs, WorkflowReadCallsJson.Default.JsonElement,
            (args, call) => ReadAsync(call, args.Machine, "runs",
                JsonSerializer.SerializeToElement(args with { Machine = null }, WorkflowReadCallsJson.Default.WorkflowRunsArgs),
                () => runtime.Reader.WorkflowRuns(JsonSerializer.SerializeToElement(args with { Machine = null }, WorkflowReadCallsJson.Default.WorkflowRunsArgs), call.Cancellation)), bulk: true);
        calls.Read("workflow", "run", WorkflowReadCallsJson.Default.WorkflowRunArgs, WorkflowReadCallsJson.Default.JsonElement,
            (args, call) => ReadAsync(call, args.Machine, "run",
                JsonSerializer.SerializeToElement(args with { Machine = null }, WorkflowReadCallsJson.Default.WorkflowRunArgs),
                () => runtime.Reader.WorkflowRun(Required(args.RunId), args.After, call.Cancellation)), bulk: true);
        calls.Read("workflow", "history", WorkflowReadCallsJson.Default.WorkflowHistoryArgs, WorkflowReadCallsJson.Default.JsonElement,
            (args, call) => ReadAsync(call, args.Machine, "history",
                JsonSerializer.SerializeToElement(args with { Machine = null }, WorkflowReadCallsJson.Default.WorkflowHistoryArgs),
                () => runtime.Reader.WorkflowHistory(Required(args.RunId), args.After, args.Limit, call.Cancellation)), bulk: true);
        calls.Read("workflow", "receipt", WorkflowReadCallsJson.Default.WorkflowReceiptArgs, WorkflowReadCallsJson.Default.JsonElement,
            (args, call) => ReadAsync(call, args.Machine, "receipt",
                JsonSerializer.SerializeToElement(args with { Machine = null }, WorkflowReadCallsJson.Default.WorkflowReceiptArgs),
                () => runtime.Reader.WorkflowReceipt(Required(args.ReceiptId), args.After, args.Limit, call.Cancellation)), bulk: true);
    }

    private async Task<JsonElement> ReadAsync(CallContext call, string? machine, string method, JsonElement args, Func<JsonElement> local)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
        if (machine is not null && string.IsNullOrWhiteSpace(machine)) throw new CallBadArguments("bad_args");
        if (machine is not null && machine != runtime.Machine)
            return await (forward ?? throw new KeyNotFoundException("Unknown remote engine machine"))
                .CallAsync(machine, "workflow", method, args, true, call.Cancellation).ConfigureAwait(false);
        try { return local(); }
        catch (ArgumentException error) { throw new CallBadArguments(error.Message); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("workflow_", StringComparison.Ordinal))
        { throw new CallFailed(error.Message); }
    }

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("bad_args");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(WorkflowRunsArgs))]
[JsonSerializable(typeof(WorkflowRunArgs))]
[JsonSerializable(typeof(WorkflowHistoryArgs))]
[JsonSerializable(typeof(WorkflowReceiptArgs))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class WorkflowReadCallsJson : JsonSerializerContext;
