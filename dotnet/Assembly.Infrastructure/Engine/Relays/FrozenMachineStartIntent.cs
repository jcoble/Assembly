using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Relays;

internal enum StartIssuePhase { NotIssued, Sending, FrameOrdered, Resolved, DetachedUnknown }
internal enum StartMissingProof { None, NotIssued, SameOriginalOrderedBarrier, DetachedUnknown }
internal sealed record MachineStartResolution(MachineStartAck Evidence, StartMissingProof MissingProof);

/// <summary>One invocation's immutable full Start intent and its actual local send evidence; never a wire capability.</summary>
internal sealed class FrozenMachineStartIntent
{
    internal RemoteMachine Owner { get; private set; }
    internal RemoteMachine.OwnerConnectionBinding Binding { get; private set; }
    internal EngineMirror.LiveMirrorBinding? Live { get; private set; }
    internal MachineCommand Command { get; }
    internal MachineStartAckArgs Ack { get; }
    internal StartTitleBeginArgs TitleRequest { get; }
    private readonly byte[] _facts;
    private readonly byte[] _title;
    internal ReadOnlyMemory<byte> FactsBytes => _facts;
    internal ReadOnlyMemory<byte> TitleBytes => _title;
    internal bool OriginalBulk { get; private set; }
    internal SemaphoreSlim Operation { get; } = new(1);
    internal StartIssuePhase Phase { get; private set; }
    internal ClientWebSocket? OriginalSocket { get; private set; }
    internal long OriginalCallId { get; private set; }
    internal CommandResult? OriginalAnswer { get; set; }
    private readonly TaskCompletionSource _ordered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task WhenFrameOrdered => _ordered.Task;

    private FrozenMachineStartIntent(RemoteMachine owner, RemoteMachine.OwnerConnectionBinding binding,
        MachineCommand request, PreparedSessionTitleCommand prepared, EngineMirror.LiveMirrorBinding? live)
    {
        Owner = owner; Binding = binding; Live = live;
        Command = request with { Command = prepared.Command };
        Ack = new(request.CommandId, request.FromMachine, request.EntityId, request.ExpectedVersion, request.Epoch, MachineStartCanonical.FromPrepared(prepared));
        _facts = StartTitlePayload.Facts((StartSession)prepared.Command); _title = prepared.TitleUtf8!;
        TitleRequest = new(1, binding.Machine, Ack, new(_facts.LongLength, RelayHash.Sha(_facts)), new(_title.LongLength, RelayHash.Sha(_title)));
    }
    internal static FrozenMachineStartIntent Create(RemoteMachine owner, RemoteMachine.OwnerConnectionBinding binding,
        MachineCommand command, EngineMirror.LiveMirrorBinding? live = null)
    {
        if (command.Command is not StartSession start || start.Title is null || command.CommandId == Guid.Empty || !Guid.TryParse(command.EntityId, out _) ||
            command.ExpectedVersion != 0 || command.Epoch != 0 || string.IsNullOrWhiteSpace(command.FromMachine) || command.FromMachine == binding.Machine ||
            live is not null && !ReferenceEquals(live.OwnerBinding, binding)) throw new ArgumentException("bad_args");
        return new(owner, binding, command, EngineCommands.PrepareSessionTitle(start), live);
    }

    internal void AdmitTransport(RemoteMachine owner, RemoteMachine.OwnerConnectionBinding binding, EngineMirror.LiveMirrorBinding? live)
    {
        if (Phase != StartIssuePhase.NotIssued || binding.Machine != Binding.Machine ||
            live is not null && !ReferenceEquals(live.OwnerBinding, binding)) throw new CallFailed("relay_connection_changed");
        Owner = owner; Binding = binding; Live = live;
    }

    // Called only under the actual creating owner's existing send gate, immediately before emitting a full Start frame.
    internal void SendingNormal(RemoteMachine owner, ClientWebSocket socket, string feature, string method, JsonElement args, long callId)
    {
        if (Phase != StartIssuePhase.NotIssued || !ReferenceEquals(owner, Owner) || feature != "engine" || method != "machine_command" ||
            args.GetRawText() != JsonSerializer.Serialize(Command, EngineSocketJson.Default.MachineCommand))
            throw new InvalidOperationException("Start issue binding mismatch.");
        OriginalSocket = socket; OriginalCallId = callId; OriginalBulk = false; Phase = StartIssuePhase.Sending;
    }
    internal void SendingBulk(RemoteMachine owner, ClientWebSocket socket, string feature, string method, JsonElement args, long callId)
    {
        if (Phase != StartIssuePhase.NotIssued || !ReferenceEquals(owner, Owner) || feature != "engine" || method != "start_title_commit" ||
            args.Deserialize(EngineSocketJson.Default.StartTitleCommitArgs)?.Request != TitleRequest)
            throw new InvalidOperationException("Start issue binding mismatch.");
        OriginalSocket = socket; OriginalCallId = callId; OriginalBulk = true; Phase = StartIssuePhase.Sending;
    }
    internal void FrameOrdered()
    {
        if (Phase != StartIssuePhase.Sending) throw new InvalidOperationException("Start was not sending.");
        Phase = StartIssuePhase.FrameOrdered; _ordered.TrySetResult();
    }
    internal void SendFailed() { if (Phase == StartIssuePhase.Sending) Phase = StartIssuePhase.DetachedUnknown; }
    internal bool IsOriginalBarrier(RemoteMachine owner, RemoteMachine.OwnerConnectionBinding binding) =>
        Phase == StartIssuePhase.FrameOrdered && ReferenceEquals(owner, Owner) && ReferenceEquals(binding, Binding) &&
        !Binding.IsInvalidated && Live?.IsInvalidated != true && OriginalSocket is { State: WebSocketState.Open };
    internal MachineStartResolution Resolve(MachineStartAck ack, bool originalBarrier)
    {
        if (OriginalAnswer is not null && ack.State is MachineStartAckState.Accepted or MachineStartAckState.Refused && ack.Recorded != OriginalAnswer)
            return new(new(MachineStartAckState.Unknown, null, null), StartMissingProof.None);
        if (ack.State is MachineStartAckState.Accepted or MachineStartAckState.Refused or MachineStartAckState.Conflict)
        { Phase = StartIssuePhase.Resolved; return new(ack, StartMissingProof.None); }
        if (ack.State != MachineStartAckState.Missing) return new(ack, StartMissingProof.None);
        if (OriginalAnswer is not null) return new(new(MachineStartAckState.Unknown, null, null), StartMissingProof.None);
        if (Phase == StartIssuePhase.NotIssued) return new(ack, StartMissingProof.NotIssued);
        if (originalBarrier) { Phase = StartIssuePhase.NotIssued; return new(ack, StartMissingProof.SameOriginalOrderedBarrier); }
        Phase = StartIssuePhase.DetachedUnknown;
        return new(new(MachineStartAckState.Unknown, null, null), StartMissingProof.DetachedUnknown);
    }
}
