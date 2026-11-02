using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class MachineRelay
{
    private async Task RelayOne(Candidate candidate, Endpoint source, Endpoint target, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, source.Live?.Invalidated ?? default, target.Live?.Invalidated ?? default);
        ct = stop.Token;
        Dictionary<string, RemoteMachine.RelayBulkLease> leases = new(StringComparer.Ordinal);
        Source? opened = null;
        try
        {
            foreach (var owner in new[] { source, target }.Where(owner => owner.Remote is not null).OrderBy(owner => owner.Machine, StringComparer.Ordinal))
            {
                await owner.RequireCurrent(ct);
                leases.Add(owner.Machine, await owner.Remote!.AcquireRelayBulkAsync(owner.Live!.OwnerBinding, ct, owner.Live));
            }
            opened = await OpenSource(candidate, source, target, leases, ct);
            if (!RelayDigests.CanSend(opened.Descriptor)) return;
            var ack = await ReadAck(target, opened.Descriptor, ct);
            if (ack.Unknown)
            {
                var original = opened.Descriptor;
                await opened.DisposeAsync(); opened = null;
                await RegisterSender(candidate, source, target, leases, ct);
                opened = await OpenSource(candidate, source, target, leases, ct);
                if (opened.Descriptor != original) throw new CallFailed("source_changed");
                ack = await ReadAck(target, original, ct);
            }
            if (ack.Unknown) return;
            // Read both fixed receipt slots before choosing a fresh root or its one stale-epoch successor.
            for (var slot = 0; slot < 2 && !ack.DestinationPresent; slot++)
            {
                var selected = ack.Retry ?? ack.First;
                if (selected is { Applied: false } && (ack.Retry is not null || selected.Error != "stale_epoch")) break;
                if (selected?.Applied == true) return;
                var retry = ack.First is { Applied: false, Error: "stale_epoch" };
                var epoch = ack.Destination?.RecipientEpoch;
                if (epoch is null || ack.Destination is not { RecipientHolder: "local", RecipientReservedEpoch: null }) return;
                var request = new RelaySendRequest(opened.Descriptor, epoch.Value, retry ? ack.FirstKey : null);
                ack = await Send(target, leases.GetValueOrDefault(target.Machine), opened, request, ct);
                if (ack.Unknown) return;
            }
            var outcome = Outcome(ack);
            if (outcome is null) return;
            await opened.RequireCurrent(ct); await source.RequireCurrent(ct); await target.RequireCurrent(ct);
            var descriptor = opened.Descriptor;
            var args = new RelayOutcomeArgs(target.Machine, descriptor, descriptor.IdentitySha256, descriptor.Fence.MessageVersion,
                descriptor.Fence.RecipientStubEpoch, outcome, ack);
            if (source.Remote is null) _engine.Write(scope => _runtime.Commands.ApplyRelayOutcome(scope, args, Now));
            else await _forward.RelayOutcomeAsync(source.Remote, source.Live!.OwnerBinding, args, ct, source.Live);
        }
        finally
        {
            if (opened is not null)
                try { await opened.DisposeAsync(); } catch (Exception error) { Diagnostic(error); }
            foreach (var lease in leases.Values.Reverse())
                try { await lease.DisposeAsync(); } catch (Exception error) { Diagnostic(error); }
        }
    }
    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private static MessageOutcome? Outcome(RelaySendAck ack)
    {
        if (ack.Unknown) return null;
        if (ack.DestinationPresent)
            return ack.Destination is { State: "delivered_to_turn" or "next_turn" or "unsupported" or "failed" } found
                ? new(found.State!, found.ProviderTurnId, found.Reason) : null;
        var refused = ack.Retry ?? ack.First;
        return refused is { Applied: false, Error: not null } && (ack.Retry is not null || refused.Error != "stale_epoch")
            ? new("failed", null, refused.Error) : null;
    }
    private Task<RelaySendAck> ReadAck(Endpoint target, RelaySourceDescriptor source, CancellationToken ct) => target.Remote is null
        ? Task.FromResult(_engine.Read(scope => _runtime.Commands.ReadRelayAck(scope, source), ct))
        : _forward.RelayAckAsync(target.Remote, target.Live!.OwnerBinding, source, ct, target.Live);

    private async Task<RelaySendAck> Send(Endpoint target, RemoteMachine.RelayBulkLease? lease, Source source, RelaySendRequest request, CancellationToken ct)
    {
        string? transfer = null;
        try
        {
            var begin = target.Remote is null
                ? _engine.Read(scope => _runtime.Commands.ProbeRelaySend(scope, request), ct)
                : await _forward.RelayBeginAsync(lease!, request, ct);
            if (begin.Status != "parts_required") return begin.Ack;
            transfer = begin.Transfer;
            var complete = new byte[2][]; var index = 0;
            foreach (var kind in new[] { RelayPartKind.Body, RelayPartKind.SenderLabel })
            {
                var manifest = kind == RelayPartKind.Body ? source.Descriptor.Body : source.Descriptor.SenderLabel;
                using var segments = new RelaySegments(manifest);
                while (segments.Length < manifest.Utf8Bytes)
                {
                    var part = await source.Read(kind, segments.Length, ct);
                    if (part.Offset != segments.Length || part.Total != manifest.Utf8Bytes || part.Next != checked(part.Offset + part.Data.LongLength)) throw new CallFailed("bad_parts");
                    segments.Append(part.Offset, part.Data, ct);
                    if (target.Remote is not null)
                    {
                        var accepted = await _forward.RelayPartAsync(lease!, new(transfer!, kind, part.Offset, part.Data), ct);
                        if ((kind == RelayPartKind.Body ? accepted.BodyNext : accepted.SenderLabelNext) != part.Next) throw new CallFailed("bad_parts");
                    }
                }
                complete[index++] = segments.Complete(ct);
            }
            var prepared = PreparedRelaySend.Create(source.Descriptor, complete[0], complete[1]);
            await source.RequireCurrent(ct); await target.RequireCurrent(ct);
            return target.Remote is null ? _engine.Write(scope => _runtime.Commands.ApplyRelaySend(scope, request, prepared, Now))
                : await _forward.RelayCommitAsync(lease!, new(request, transfer), ct);
        }
        finally { if (transfer is not null) await _forward.DropRelayTargetAsync(lease!, transfer, CancellationToken.None); }
    }

    private async Task RegisterSender(Candidate candidate, Endpoint source, Endpoint target, IReadOnlyDictionary<string, RemoteMachine.RelayBulkLease> leases, CancellationToken ct)
    {
        ForeignAncestor node;
        if (source.Remote is null)
        {
            var captured = _runtime.Reader.ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, candidate.From, target.Machine), ct);
            _runtime.Reader.CheckForeignTitleSource(captured, ct); node = captured.Nodes.Single();
        }
        else
        {
            var lease = leases[source.Machine];
            var captured = await _forward.ReadForeignTitlesAsync(lease, new(ForeignTitleSourceMode.NodeOnly, candidate.From, target.Machine), ct);
            try { await _forward.CheckForeignTitlesAsync(lease, captured.Opened, ct); node = captured.Nodes.Single(); }
            finally { await _forward.CloseForeignTitlesAsync(lease, captured.Opened.Handle, CancellationToken.None); }
        }
        if (node.SessionId != candidate.From || node.Machine != source.Machine) throw new CallFailed("source_changed");
        var id = new Guid(SHA256.HashData(RelayHash.Utf8.GetBytes(candidate.Message + "\nsender")).AsSpan(0, 16));
        var command = new MachineCommand(id, source.Machine, candidate.From, 0, 0, new RegisterForeignSession(source.Machine, node.Provider, node.Title));
        await source.RequireCurrent(ct); await target.RequireCurrent(ct);
        if (target.Remote is null)
        {
            var prepared = EngineCommands.PrepareSessionTitle((RegisterForeignSession)command.Command);
            _engine.Write(scope => _runtime.Commands.ApplyPreparedSessionTitle(scope,
                new(id, target.Machine, new(ActorKind.System, "machine:" + source.Machine), candidate.From, 0, 0, Now), prepared));
        }
        else await _forward.RegisterForeignTitleAsync(leases[target.Machine], command, ct);
        // Only the following owning ACK can certify sender/recipient ownership. Metadata replay is not delivery evidence.
    }

    private async Task<Source> OpenSource(Candidate candidate, Endpoint source, Endpoint target, IReadOnlyDictionary<string, RemoteMachine.RelayBulkLease> leases, CancellationToken ct)
    {
        await source.RequireCurrent(ct); await target.RequireCurrent(ct);
        Source opened;
        if (source.Remote is null)
        {
            var captured = _runtime.Reader.RelaySource(new(candidate.Message, target.Machine), ct);
            opened = new(this, source, captured, null, null, RelayDigests.Describe(captured));
        }
        else
        {
            var lease = leases[source.Machine];
            var captured = await _forward.OpenRelaySourceAsync(lease, new(candidate.Message, target.Machine), ct);
            opened = new(this, source, null, lease, captured.Handle, captured.Source);
        }
        try
        {
            var descriptor = opened.Descriptor; RelayDigests.Validate(descriptor);
            if (descriptor.FromMachine != source.Machine || descriptor.RecipientMachine != target.Machine || descriptor.MessageId != candidate.Message ||
                descriptor.FromSessionId != candidate.From || descriptor.ToSessionId != candidate.To) throw new CallFailed("source_changed");
            return opened;
        }
        catch { await opened.DisposeAsync(); throw; }
    }
    private sealed class Source(MachineRelay owner, Endpoint endpoint, RelaySourceCapture? local, RemoteMachine.RelayBulkLease? lease, string? handle,
        RelaySourceDescriptor descriptor) : IAsyncDisposable
    {
        internal RelaySourceDescriptor Descriptor => descriptor;
        private byte[]? _body, _label;
        internal Task<RelaySourcePart> Read(RelayPartKind kind, long offset, CancellationToken ct)
        {
            if (local is null) return owner._forward.ReadRelaySourcePartAsync(lease!, new(handle!, kind, offset), ct);
            ct.ThrowIfCancellationRequested();
            var bytes = kind == RelayPartKind.Body ? _body ??= RelayHash.Utf8.GetBytes(local.Identity.Command.Text) : _label ??= RelayHash.Utf8.GetBytes(local.SenderLabel);
            var take = (int)Math.Min(RelaySegments.ChunkBytes, bytes.LongLength - offset);
            return Task.FromResult(new RelaySourcePart(bytes.AsSpan(checked((int)offset), take).ToArray(), offset, offset + take, bytes.LongLength));
        }
        internal async Task RequireCurrent(CancellationToken ct)
        {
            await endpoint.RequireCurrent(ct);
            var current = local is null ? await owner._forward.CheckRelaySourceAsync(lease!, handle!, ct)
                : RelayDigests.Describe(owner._runtime.Reader.RelaySource(new(descriptor.MessageId, descriptor.RecipientMachine), ct));
            if (current != descriptor) throw new CallFailed("source_changed");
        }
        public ValueTask DisposeAsync() => handle is null ? ValueTask.CompletedTask : new(owner._forward.CloseRelaySourceAsync(lease!, handle, CancellationToken.None));
    }
}
