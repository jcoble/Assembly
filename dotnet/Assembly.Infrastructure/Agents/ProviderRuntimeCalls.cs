using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Agents;

/// <summary>Owner-only settings on the selected engine's existing connection and descriptor authority.</summary>
internal sealed class ProviderRuntimeCalls(string localMachine, AgentsFile? store, MachineForward? forward = null,
    ProviderRuntimeSubscriptions? subscriptions = null) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        var json = ProviderRuntimeJson.Default;
        calls.Read("providers", "runtimeRead", json.ProviderRuntimeReadArgs, json.ProviderRuntimeReply, ReadAsync);
        calls.Write("providers", "runtimeSave", json.ProviderRuntimeSaveArgs, json.ProviderRuntimeReply,
            async (args, call) =>
            {
                var reply = await SaveAsync(args, call);
                return _ => reply;
            }, order: args => $"providers/runtime/{args?.Machine?.Length ?? 0}:{args?.Machine}/{args?.Provider?.Length ?? 0}:{args?.Provider}");
        if (subscriptions is null)
            calls.Topic(AgentsFile.Topic, json.ProviderRuntimeChanged, item => item.Machine, State);
        else
            calls.Topic(AgentsFile.Topic, json.ProviderRuntimeChanged, item => item.Machine, State,
                (key, call) => { RequireCaller(call); if (key != localMachine) subscriptions.Opened(key); }, CaptureSource);
    }

    private IReadOnlyList<ProviderRuntimeChanged> State(string? key, CallContext call)
    {
        RequireCaller(call);
        if (string.IsNullOrEmpty(key)) throw Refusal("bad_args");
        if (key == localMachine) return [LocalStore().Current()];
        if (subscriptions is null) throw Refusal("wrong_machine");
        return call.SourceGuard is ProviderRuntimeSubscriptions.Projection projection ? [projection.Value] : [];
    }

    private IExpectedSourceGuard? CaptureSource(string? key, CallContext call)
    {
        RequireCaller(call);
        if (string.IsNullOrEmpty(key)) throw Refusal("bad_args");
        return key == localMachine ? LocalDelivery.Instance : subscriptions!.Capture(key);
    }

    private sealed class LocalDelivery : IExpectedSourceGuard
    {
        internal static readonly LocalDelivery Instance = new();
        public CancellationToken Changed => CancellationToken.None;
        public void EnsureCurrent() { }
        public void Dispose() { }
    }

    internal async Task<ProviderRuntimeReply> ReadAsync(ProviderRuntimeReadArgs args, CallContext call)
    {
        RequireCaller(call);
        if (args is null || string.IsNullOrEmpty(args.Machine) || string.IsNullOrEmpty(args.Provider))
            throw Refusal("bad_args");
        if (args.Machine == localMachine) return LocalStore().Read(args.Provider, call.Cancellation);
        if (forward is null) throw Refusal("wrong_machine");
        var value = await forward.CallAsync(args.Machine, "providers", "runtimeRead",
            JsonSerializer.SerializeToElement(args, ProviderRuntimeJson.Default.ProviderRuntimeReadArgs), false, call.Cancellation);
        return Reply(value, args.Machine, args.Provider);
    }

    internal async Task<ProviderRuntimeReply> SaveAsync(ProviderRuntimeSaveArgs args, CallContext call)
    {
        RequireCaller(call);
        if (args is null || string.IsNullOrEmpty(args.Machine) || string.IsNullOrEmpty(args.Provider) ||
            string.IsNullOrEmpty(args.ExpectedRevision) || args.Settings is null)
            throw Refusal("bad_args");
        if (args.Machine == localMachine)
            return LocalStore().Save(args.Provider, args.ExpectedRevision, args.Settings, call.Cancellation);
        if (forward is null) throw Refusal("wrong_machine");
        var value = await forward.CallAsync(args.Machine, "providers", "runtimeSave",
            JsonSerializer.SerializeToElement(args, ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs), false, call.Cancellation);
        return Reply(value, args.Machine, args.Provider);
    }

    private AgentsFile LocalStore()
    {
        if (store is null) throw Refusal("configuration_unavailable");
        if (store.Current().Machine != localMachine) throw Refusal("wrong_machine");
        return store;
    }

    private static ProviderRuntimeReply Reply(JsonElement value, string machine, string provider)
    {
        ProviderRuntimeReply? reply;
        try { reply = value.Deserialize(ProviderRuntimeJson.Default.ProviderRuntimeReply); }
        catch (JsonException) { throw Refusal("configuration_unavailable"); }
        if (reply is null) throw Refusal("configuration_unavailable");
        if (reply.Machine != machine || reply.Provider != provider) throw Refusal("wrong_machine");
        return reply;
    }

    private static void RequireCaller(CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw Refusal("forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
    }

    private static CallFailed Refusal(string reason) => new("Provider runtime settings are unavailable.", reason);
}
