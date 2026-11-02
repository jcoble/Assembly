using Assembly.Infrastructure.Engine.Relays;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineForward
{
    internal Task<ForeignTitleReply> ForeignAncestryTitlesAsync(RemoteMachine.RelayBulkLease lease, ForeignAncestryArgs args, CancellationToken ct)
    {
        var (facts, titles) = ForeignTitlePayload.Encode(args.RootToParent);
        var proof = new ForeignAncestryProofQuery(args.FromMachine, args.ParentSessionId, args.RootToParent.Count,
            ForeignTitleDigests.Structure(lease.Machine, args.FromMachine, args.ParentSessionId, args.RootToParent));
        var request = new ForeignTitleRequest(ForeignTitleOperation.ForeignAncestry, lease.Machine, args.FromMachine, args.ParentSessionId,
            null, proof, args.RootToParent.Count, new(facts.LongLength, RelayHash.Sha(facts)), new(titles.LongLength, RelayHash.Sha(titles)));
        return ApplyForeignTitlesAsync(lease, request, facts, titles, ct);
    }
    internal Task<ForeignTitleReply> RegisterForeignTitleAsync(RemoteMachine.RelayBulkLease lease, MachineCommand command, CancellationToken ct)
    {
        if (command.Command is not RegisterForeignSession foreign || foreign.Machine != command.FromMachine) throw new CallFailed("bad_args");
        var prepared = EngineCommands.PrepareSessionTitle(foreign);
        var query = new ForeignRegisterReceiptQuery(command.CommandId, command.FromMachine, command.EntityId, command.ExpectedVersion, command.Epoch,
            RegisterTitleCanonical(prepared.CanonicalJournal));
        var (facts, titles) = ForeignTitlePayload.Encode([new(command.EntityId, foreign.Machine, foreign.Provider, foreign.Title, null)]);
        var request = new ForeignTitleRequest(ForeignTitleOperation.RegisterForeign, lease.Machine, command.FromMachine, command.EntityId,
            query, null, 1, new(facts.LongLength, RelayHash.Sha(facts)), new(titles.LongLength, RelayHash.Sha(titles)));
        return ApplyForeignTitlesAsync(lease, request, facts, titles, ct);
    }
    internal async Task<ForeignTitleReply> ApplyForeignTitlesAsync(RemoteMachine.RelayBulkLease lease, ForeignTitleRequest request,
        byte[] facts, byte[] titles, CancellationToken ct)
    {
        var frozen = ForeignTitlePayload.Freeze(request);
        if (frozen.TargetMachine != lease.Machine) throw new CallFailed("wrong_machine");
        string? handle = null;
        try
        {
            var begin = await TitleCall(lease, "foreign_title_begin", frozen, EngineSocketJson.Default.ForeignTitleRequest, EngineSocketJson.Default.ForeignTitleReply, ct);
            if (begin.Kind != ForeignTitleReplyKind.PartsRequired) return begin;
            handle = begin.Handle ?? throw new CallFailed("bad_handle");
            foreach (var (kind, bytes) in new[] { (ForeignTitlePartKind.NodeFacts, facts), (ForeignTitlePartKind.Titles, titles) })
                for (var offset = 0; offset < bytes.Length; offset += RelaySegments.ChunkBytes)
                {
                    var data = bytes.AsSpan(offset, Math.Min(RelaySegments.ChunkBytes, bytes.Length - offset)).ToArray();
                    var next = await TitleCall(lease, "foreign_title_part", new ForeignTitleAppendArgs(handle, kind, offset, data),
                        EngineSocketJson.Default.ForeignTitleAppendArgs, EngineSocketJson.Default.ForeignTitleOffsets, ct);
                    if ((kind == ForeignTitlePartKind.NodeFacts ? next.NodeFactsNext : next.TitlesNext) != offset + data.LongLength) throw new CallFailed("bad_parts");
                }
            return await TitleCall(lease, "foreign_title_commit", new ForeignTitleCommitArgs(frozen, handle),
                EngineSocketJson.Default.ForeignTitleCommitArgs, EngineSocketJson.Default.ForeignTitleReply, ct);
        }
        catch { await lease.AbortAsync().ConfigureAwait(false); throw; }
        finally
        {
            if (handle is not null)
            {
                try { await TitleCall(lease, "foreign_title_drop", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean, ct); }
                catch { await lease.AbortAsync().ConfigureAwait(false); }
            }
        }
    }
    private static ForeignRegisterCanonical RegisterTitleCanonical(string canonical)
    {
        using var json = JsonDocument.Parse(canonical); var root = json.RootElement;
        return root.TryGetProperty("titleJournalVersion", out _) ? new(null, root.GetProperty("commandSha256").GetString(),
            root.GetProperty("commandUtf8Bytes").GetInt64(), root.GetProperty("titleUtf8Bytes").GetInt64()) : new(canonical, null, null, null);
    }
}
