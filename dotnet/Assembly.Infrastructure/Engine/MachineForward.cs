using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine.Workflows;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Handoffs;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Engine;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

/// <summary>Forwards through the existing owner's sockets without creating local engine state.</summary>
internal sealed partial class MachineForward(Func<string, RemoteMachine?> byMachineId)
{
    public Task<OwnerPromptPage> OwnerPromptsAsync(string machine, OwnerPromptReadArgs args,
        Func<OwnerPromptPage, int> encodedBytes, CancellationToken ct)
    {
        var remote = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
        return remote.ReadOwnerPromptPageAsync(args, encodedBytes, machine, ct);
    }

    public async Task<CommandResult> CommandAsync(string machine, WindowCommand command, CancellationToken ct)
    {
        var auth = command.Command is CheckProvider or StartSignIn or CancelSignIn;
        var args = JsonSerializer.SerializeToElement(command with { Machine = auth ? machine : null }, EngineSocketJson.Default.WindowCommand);
        var result = await CallAsync(machine, "engine", "command", args, false, ct);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid engine command result");
    }

    public async Task<StartLocation> StartLocationAsync(string machine, string parentSessionId, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(new StartLocationArgs(parentSessionId), EngineSocketJson.Default.StartLocationArgs);
        var result = await CallAsync(machine, "engine", "start_location", args, false, ct);
        return result.Deserialize(EngineSocketJson.Default.StartLocation) ?? throw new IOException("Invalid start location result");
    }

    public async Task<CommandResult> MachineCommandAsync(string machine, MachineCommand command, CancellationToken ct)
    {
        if (command.Command is RegisterForeignSession && !FitsMachineFrame("machine_command", command, EngineSocketJson.Default.MachineCommand))
        {
            var owner = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
            var binding = await owner.CaptureOwnerBindingAsync(machine, ct);
            await using var lease = await owner.AcquireRelayBulkAsync(binding, ct);
            return TitleResult(await RegisterForeignTitleAsync(lease, command, ct));
        }
        var args = JsonSerializer.SerializeToElement(command, EngineSocketJson.Default.MachineCommand);
        var result = await CallAsync(machine, "engine", "machine_command", args, false, ct);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid machine command result");
    }

    public async Task<JsonElement> CallAsync(string machine, string feature, string method, JsonElement args, bool bulk, CancellationToken ct,
        ClientWebSocket? expectedConnection = null, RemoteMachine.OwnerConnectionBinding? expectedOwner = null,
        EngineMirror.LiveMirrorBinding? expectedMirror = null, bool gitWrite = false)
    {
        ct.ThrowIfCancellationRequested();
        var remote = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
        try
        {
            if (feature == "messages" && method is "body" or "match")
            {
                if (!bulk) throw new Assembly.Application.Common.Calls.CallBadArguments("Message authority reads require bulk.");
                var captured = await remote.CaptureOwnerReadAsync(machine, ct).ConfigureAwait(false);
                return await remote.CallAsync(feature, method, args, true, ct, machine, captured.Socket, expectedOwner, expectedMirror).ConfigureAwait(false);
            }
            return feature == "engine" && method is "session_title" or "session_title_chunk" or "entities"
            ? await remote.ReadOwnerPromptAsync(method, args, bulk, machine, ct)
            : gitWrite && feature == "git" && expectedConnection is not null
                ? await remote.CallGitWriteAsync(method, args, machine, expectedConnection, ct, expectedOwner, expectedMirror)
                : await remote.CallAsync(feature, method, args, bulk, ct, machine, expectedConnection, expectedOwner, expectedMirror);
        }
        catch (MachineUnreachable) when (feature == "engine" && method is "session_title" or "session_title_chunk" or "entities")
        { throw new Assembly.Application.Common.Calls.CallFailed(method == "entities" ? "entities_changed" : "title_changed"); }
        catch (Exception error) when (error is WebSocketException or IOException or HttpRequestException or ObjectDisposedException)
        {
            throw new MachineUnreachable(remote.Name!, $"{remote.Name}: machine socket closed", error);
        }
    }
    internal async Task<bool> MessageSourceAvailableAsync(string machine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var remote = byMachineId(machine);
            if (remote is null) return false;
            await remote.CaptureOwnerReadAsync(machine, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is MachineUnreachable or KeyNotFoundException or IOException or ObjectDisposedException or WebSocketException)
        { return false; }
    }

    internal Task<HandoffAck> HandoffAckAsync(EngineMirror.LiveMirrorBinding live, HandoffIdentity identity, CancellationToken ct) =>
        HandoffAckAsync(HandoffOwner(live, ct), live.OwnerBinding, identity, ct, live);

    internal Task<HandoffAck> HandoffPhaseAsync(EngineMirror.LiveMirrorBinding live, HandoffPhaseArgs args, CancellationToken ct) =>
        HandoffPhaseAsync(HandoffOwner(live, ct), live.OwnerBinding, args, ct, live);

    internal Task<HandoffAck> HandoffAdoptAsync(EngineMirror.LiveMirrorBinding live, HandoffAdoptArgs args, CancellationToken ct) =>
        HandoffAdoptAsync(HandoffOwner(live, ct), live.OwnerBinding, args, ct, live);

    internal Task<HandoffAck> HandoffSealAsync(EngineMirror.LiveMirrorBinding live, HandoffSealArgs args, CancellationToken ct) =>
        HandoffSealAsync(HandoffOwner(live, ct), live.OwnerBinding, args, ct, live);

    private RemoteMachine HandoffOwner(EngineMirror.LiveMirrorBinding live, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (live is null || live.IsInvalidated) throw new CallFailed("mirror_changed");
        return byMachineId(live.Machine) ?? throw new MachineUnreachable(live.Machine, "Remote engine machine is disconnected");
    }

    internal async Task<HandoffAck> HandoffAckAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        HandoffIdentity identity, CancellationToken ct, EngineMirror.LiveMirrorBinding live)
    {
        RequireHandoffBinding(binding, identity.SourceMachine, identity.TargetMachine);
        var result = await remote.CallOwnerBindingAsync(binding, "handoff_ack",
            JsonSerializer.SerializeToElement(identity, EngineSocketJson.Default.HandoffIdentity), ct, live).ConfigureAwait(false);
        return HandoffReply(result, identity);
    }

    internal async Task<HandoffAck> HandoffPhaseAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        HandoffPhaseArgs args, CancellationToken ct, EngineMirror.LiveMirrorBinding live)
    {
        RequireHandoffBinding(binding, args.Identity.SourceMachine);
        var result = await remote.CallOwnerBindingAsync(binding, "handoff_phase",
            JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.HandoffPhaseArgs), ct, live).ConfigureAwait(false);
        return HandoffReply(result, args.Identity);
    }

    internal async Task<HandoffAck> HandoffAdoptAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        HandoffAdoptArgs args, CancellationToken ct, EngineMirror.LiveMirrorBinding live)
    {
        RequireHandoffBinding(binding, args.Identity.TargetMachine);
        var result = await remote.CallOwnerBindingAsync(binding, "handoff_adopt",
            JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.HandoffAdoptArgs), ct, live).ConfigureAwait(false);
        return HandoffReply(result, args.Identity);
    }

    internal async Task<HandoffAck> HandoffSealAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        HandoffSealArgs args, CancellationToken ct, EngineMirror.LiveMirrorBinding live)
    {
        RequireHandoffBinding(binding, args.Identity.TargetMachine);
        var result = await remote.CallOwnerBindingAsync(binding, "handoff_seal",
            JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.HandoffSealArgs), ct, live).ConfigureAwait(false);
        return HandoffReply(result, args.Identity);
    }

    private static void RequireHandoffBinding(RemoteMachine.OwnerConnectionBinding binding, string machine, string? alternative = null)
    {
        if (binding.Machine != machine && binding.Machine != alternative) throw new CallFailed("wrong_machine");
    }

    private static HandoffAck HandoffReply(JsonElement result, HandoffIdentity identity)
    {
        var ack = result.Deserialize(EngineSocketJson.Default.HandoffAck) ?? throw new IOException("Invalid handoff acknowledgment");
        return ack.Identity == identity ? ack : throw new IOException("Handoff acknowledgment identity changed");
    }

    // One fixed host-derived read; public project IDs are never reinterpreted as owner paths or IDs.
    internal Task<JsonElement> WorkflowDefinitionsAtRootAsync(RemoteConnections.ProjectOwner owner,
        WorkflowDefinitionsAtRootArgs args, CancellationToken ct)
    {
        if (args.Machine != owner.Engine) throw new ArgumentException("wrong_machine", nameof(args));
        return CallAsync(owner.Engine, "workflow", "definitions_at_root",
            JsonSerializer.SerializeToElement(args, WorkflowReadCallsJson.Default.WorkflowDefinitionsAtRootArgs), true, ct,
            expectedConnection: owner.Socket, expectedOwner: owner.Binding);
    }

    internal async Task<ChallengeReply?> SignInChallengeAsync(string machine, string operationId, CallContext call)
    {
        var (remote, original, source) = await CaptureSignInSourceAsync(machine, call);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, original.Invalidated);
        var args = JsonSerializer.SerializeToElement(new ChallengeArgs(operationId), ProvidersJson.Default.ChallengeArgs);
        var result = await remote.CallAsync("providers", "challenge", args, false, linked.Token, machine, expectedOwner: original);
        source.EnsureCurrent();
        if (result.ValueKind == JsonValueKind.Null) return null;
        ChallengeReply challenge;
        try
        {
            challenge = result.Deserialize(ProvidersJson.Default.ChallengeReply) ?? throw InvalidSignInChallenge();
            if (challenge.OperationId != operationId || challenge.Generation <= 0 ||
                challenge.TerminalId is not null && string.IsNullOrWhiteSpace(challenge.TerminalId)) throw InvalidSignInChallenge();
        }
        catch (JsonException) { throw InvalidSignInChallenge(); }
        if (challenge.TerminalId is { } id)
        {
            var qualified = await remote.Terminals.RegisterAsync(machine, id, original, linked.Token);
            source.EnsureCurrent();
            challenge = challenge with { TerminalId = qualified };
        }
        source.EnsureCurrent();
        return challenge;
    }

    internal async Task<ProviderInputReply> SignInCodeAsync(SubmitCodeArgs request, CallContext call)
    {
        var machine = request.Machine!;
        var (remote, original, source) = await CaptureSignInSourceAsync(machine, call);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, original.Invalidated);
        var args = JsonSerializer.SerializeToElement(request with { Machine = null }, ProvidersJson.Default.SubmitCodeArgs);
        var result = await remote.CallAsync("providers", "submitCode", args, false, linked.Token, machine, expectedOwner: original);
        source.EnsureCurrent();
        return ReadSignInInputReply(result);
    }

    internal async Task<ProviderInputReply> SignInUrlAsync(AcknowledgeUrlArgs request, CallContext call)
    {
        var machine = request.Machine!;
        var (remote, original, source) = await CaptureSignInSourceAsync(machine, call);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, original.Invalidated);
        var args = JsonSerializer.SerializeToElement(request with { Machine = null }, ProvidersJson.Default.AcknowledgeUrlArgs);
        var result = await remote.CallAsync("providers", "acknowledgeUrl", args, false, linked.Token, machine, expectedOwner: original);
        source.EnsureCurrent();
        return ReadSignInInputReply(result);
    }

    private async Task<(RemoteMachine Remote, RemoteMachine.OwnerConnectionBinding Original, SignInReplySource Source)>
        CaptureSignInSourceAsync(string machine, CallContext call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.SourceGuard is not null) throw SignInSourceChanged();
        var remote = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
        var original = await remote.CaptureOwnerBindingAsync(machine, call.Cancellation);
        var source = new SignInReplySource(original, () => ReferenceEquals(byMachineId(machine), remote));
        call.SourceGuard = source;
        source.EnsureCurrent();
        return (remote, original, source);
    }

    private static ProviderInputReply ReadSignInInputReply(JsonElement result)
    {
        try { return result.Deserialize(ProvidersJson.Default.ProviderInputReply) ?? throw InvalidSignInChallenge(); }
        catch (JsonException) { throw InvalidSignInChallenge(); }
    }

    private static CallFailed InvalidSignInChallenge() => new("Provider sign-in is unavailable.", "signin_input_failed");
    private static CallFailed SignInSourceChanged() => new("Provider sign-in is unavailable.", "source_changed");

    // The reply owns this guard until its existing socket queue sends or discards the prepared frame.
    // The connection continues to own the captured binding; disposing a reply never revokes it.
    private sealed class SignInReplySource(RemoteMachine.OwnerConnectionBinding original, Func<bool> sameOwner) : IExpectedSourceGuard
    {
        private int _disposed;
        public CancellationToken Changed => original.Invalidated;
        public void EnsureCurrent()
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || original.IsInvalidated || !sameOwner()) throw SignInSourceChanged();
            }
            catch (Exception error) when (error is KeyNotFoundException or MachineUnreachable or ObjectDisposedException)
            { throw SignInSourceChanged(); }
        }
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

}

/// <summary>Only a named read registered on its actual channel may cross the machine read door.</summary>
internal static class MachineReads
{
    internal static bool AllowRead(ICallRegistry registry, string feature, string method, bool bulk)
    {
        if (feature == "messages")
            return bulk && method is ("body" or "match") && registry.KindOf(feature, method) == CallKind.BulkRead;
        var expected = GitCallKinds.KindOf(method);
        return feature == "git" && expected == (bulk ? CallKind.BulkRead : CallKind.Read) &&
            registry.KindOf(feature, method) == expected;
    }
}
