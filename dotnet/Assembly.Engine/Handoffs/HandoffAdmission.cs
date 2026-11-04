using System.Text;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Handoffs;

public sealed record HandoffIdentity(string HandoffId, string SourceMachine, string SourceSessionId,
    string TargetMachine, long OldEpoch, long NextEpoch);
public enum HandoffPhase { Accept, Resumed, Failed }
public enum HandoffGate { Waiting, Recorded, Advanced, Refused, Unknown }
public enum HandoffTargetState { NotAdopted, Resumed, Failed }
public sealed record HandoffTargetEvidence(HandoffIdentity Identity, HandoffTargetState State,
    string AdmissionFingerprint, string? ClosedFingerprint, long SessionVersion,
    long Epoch, string? ResumeReason, string? StartEffectId, long? StartAttempt);
public sealed record HandoffPhaseArgs(HandoffIdentity Identity, HandoffPhase Phase,
    long ObservedHandoffVersion, string? Reason, HandoffTargetEvidence? Target);
public sealed record HandoffAdoptArgs(HandoffIdentity Identity, AdoptSession Command,
    string SourceAcceptFingerprint, string TransferCaptureSha256);
public sealed record HandoffSealArgs(HandoffIdentity Identity, string Reason);
public sealed record HandoffAck(HandoffIdentity Identity, HandoffGate Gate, string? Reason,
    string? ReceiptKey, string? Fingerprint, CommandResult? Receipt,
    long? HandoffVersion, string? HandoffState, HandoffTargetEvidence? Target);
public sealed record HandoffStartBinding(HandoffIdentity Identity, string AccountContext,
    string SourceAcceptFingerprint, string TransferCaptureSha256, string AdmissionFingerprint);

public enum HandoffFenceState { Ordinary, Adopted, Closed, Unknown }
public sealed record HandoffTargetFence(HandoffFenceState State, HandoffStartBinding? Binding, string? Reason);

/// <summary>Only the four fixed operation addresses belong to an actual handoff; no wake/retry creates a new slot.</summary>
internal static class HandoffAdmissionKeys
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string Identity(HandoffIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.HandoffId) || string.IsNullOrWhiteSpace(identity.SourceMachine)
            || string.IsNullOrWhiteSpace(identity.SourceSessionId) || string.IsNullOrWhiteSpace(identity.TargetMachine)
            || identity.SourceMachine == identity.TargetMachine || identity.OldEpoch < 0
            || identity.OldEpoch == long.MaxValue || identity.NextEpoch != identity.OldEpoch + 1)
            throw new ArgumentException("Invalid handoff identity.", nameof(identity));
        using var hash = new RelayHash();
        hash.String("handoff-identity/v1"); hash.String(identity.HandoffId); hash.String(identity.SourceMachine);
        hash.String(identity.SourceSessionId); hash.String(identity.TargetMachine);
        hash.Length(identity.OldEpoch); hash.Length(identity.NextEpoch);
        return hash.Finish();
    }

    internal static string SourceAccept(HandoffIdentity identity) => "handoff/v1/" + Identity(identity) + "/accept";
    internal static string SourceTerminal(HandoffIdentity identity) => "handoff/v1/" + Identity(identity) + "/terminal";
    internal static string TargetAdmission(string machine, string session) => Target(machine, session) + "/admission";
    internal static string TargetClosed(string machine, string session) => Target(machine, session) + "/closed";

    private static string Target(string machine, string session)
    {
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(session))
            throw new ArgumentException("Actual target machine and session are required.");
        return "handoff-target/v1/" + Convert.ToHexStringLower(Utf8.GetBytes(machine)) + "/"
            + Convert.ToHexStringLower(Utf8.GetBytes(session));
    }

    internal static CommandContext SourceContext(HandoffIdentity identity, HandoffPhase phase, bool accepted, long nowMs)
    {
        var key = phase == HandoffPhase.Accept ? SourceAccept(identity) : SourceTerminal(identity);
        return new(RelayReceiptKeys.ContextGuid(key), identity.SourceMachine,
            new(ActorKind.System, "machine:" + identity.TargetMachine), identity.HandoffId,
            phase == HandoffPhase.Accept || !accepted ? 1 : 2, identity.NextEpoch, nowMs);
    }

    internal static CommandContext TargetContext(HandoffIdentity identity, bool closed, long nowMs)
    {
        _ = Identity(identity);
        var key = closed ? TargetClosed(identity.TargetMachine, identity.HandoffId) : TargetAdmission(identity.TargetMachine, identity.HandoffId);
        return new(RelayReceiptKeys.ContextGuid(key), identity.TargetMachine,
            new(ActorKind.System, "machine:" + identity.SourceMachine), identity.HandoffId, 0, identity.NextEpoch, nowMs);
    }
}

/// <summary>A nonwire permission bound to one supplied writer, operation, fixed key and original canonical body.</summary>
internal enum HandoffOperation { Phase, NoAdopt, Adopt, Closed }
internal sealed class HandoffAdmission
{
    private readonly Assembly.Application.Common.Database.IWriteScope _scope;
    internal CommandContext Context { get; }
    internal string Key { get; }
    internal string Canonical { get; }
    internal HandoffIdentity Identity { get; }
    internal HandoffOutcome? Outcome { get; }
    internal HandoffOperation Operation { get; }
    internal HandoffStartBinding? Binding { get; }
    private HandoffAdmission(Assembly.Application.Common.Database.IWriteScope scope, HandoffIdentity identity,
        CommandContext context, string key, string canonical, HandoffOutcome? outcome, HandoffOperation operation, HandoffStartBinding? binding = null)
    { _scope = scope; Identity = identity; Context = context; Key = key; Canonical = canonical; Outcome = outcome; Operation = operation; Binding = binding; }

    internal static HandoffAdmission Phase(Assembly.Application.Common.Database.IWriteScope scope, HandoffIdentity identity,
        HandoffPhase phase, bool accepted, string? reason, long nowMs)
    {
        var context = HandoffAdmissionKeys.SourceContext(identity, phase, accepted, nowMs);
        var outcome = new HandoffOutcome(phase switch { HandoffPhase.Accept => "accepted", HandoffPhase.Resumed => "resumed", HandoffPhase.Failed => "failed", _ => throw new ArgumentException("Invalid phase.") }, identity.NextEpoch, reason);
        return new(scope, identity, context, phase == HandoffPhase.Accept ? HandoffAdmissionKeys.SourceAccept(identity) : HandoffAdmissionKeys.SourceTerminal(identity),
            System.Text.Json.JsonSerializer.Serialize<EngineCommand>(outcome, EngineJson.Default.EngineCommand), outcome, HandoffOperation.Phase);
    }

    internal static HandoffAdmission NoAdopt(Assembly.Application.Common.Database.IWriteScope scope, HandoffIdentity identity, long nowMs) =>
        new(scope, identity, HandoffAdmissionKeys.TargetContext(identity, false, nowMs),
            HandoffAdmissionKeys.TargetAdmission(identity.TargetMachine, identity.HandoffId), NoAdoptCanonical(identity), null, HandoffOperation.NoAdopt);

    // Called only after the owning admission door has the real P2/T9 prepared witnesses. The canonical envelope
    // excludes its derived fingerprint; the SAME journal implementation computes that value before insertion.
    internal static HandoffAdmission Adopt(Assembly.Application.Common.Database.IWriteScope scope, HandoffAdoptArgs args,
        string accountContext, long nowMs)
    {
        var context = HandoffAdmissionKeys.TargetContext(args.Identity, false, nowMs);
        var canonical = EngineJournal.HandoffAdmitCanonical(args.Identity, args.Command, accountContext,
            args.SourceAcceptFingerprint, args.TransferCaptureSha256);
        var binding = new HandoffStartBinding(args.Identity, accountContext, args.SourceAcceptFingerprint,
            args.TransferCaptureSha256, EngineJournal.HandoffFingerprint(context, canonical));
        return new(scope, args.Identity, context, HandoffAdmissionKeys.TargetAdmission(args.Identity.TargetMachine,
            args.Identity.HandoffId), canonical, null, HandoffOperation.Adopt, binding);
    }

    internal static HandoffAdmission Closed(Assembly.Application.Common.Database.IWriteScope scope, HandoffIdentity identity, string canonical, long nowMs) =>
        new(scope, identity, HandoffAdmissionKeys.TargetContext(identity, true, nowMs),
            HandoffAdmissionKeys.TargetClosed(identity.TargetMachine, identity.HandoffId), canonical, null, HandoffOperation.Closed);

    internal static string NoAdoptCanonical(HandoffIdentity identity)
    {
        using var bytes = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(bytes))
        {
            json.WriteStartObject(); json.WriteString("domain", "handoff-no-adopt/v1"); json.WritePropertyName("identity");
            System.Text.Json.JsonSerializer.Serialize(json, identity, EngineJson.Default.HandoffIdentity); json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    internal void ValidateBindings(Assembly.Application.Common.Database.IWriteScope scope, CommandContext context, string key, string canonical)
    {
        if (!ReferenceEquals(scope, _scope) || context != Context || key != Key || canonical != Canonical)
            throw new InvalidOperationException("Handoff admission belongs to a different scope or operation.");
    }
}
