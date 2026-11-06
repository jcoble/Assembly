using Assembly.Infrastructure.Engine.Relays;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

internal sealed record ForeignTitleSnapshot(ForeignTitleSourceOpened Opened, IReadOnlyList<ForeignAncestor> Nodes);

internal sealed partial class MachineForward
{
    internal async Task<ForeignTitleSnapshot> ReadForeignTitlesAsync(RemoteMachine.RelayBulkLease lease, ForeignTitleSourceOpenArgs args, CancellationToken ct)
    {
        try
        {
            var opened = await TitleCall(lease, "foreign_title_source_open", args, EngineSocketJson.Default.ForeignTitleSourceOpenArgs, EngineSocketJson.Default.ForeignTitleSourceOpened, ct);
            var manifest = opened.Manifest;
            if (manifest.SourceMachine != lease.Machine || manifest.TargetMachine != args.TargetMachine || manifest.AnchorSessionId != args.AnchorSessionId ||
                manifest.Mode != args.Mode || manifest.NodeCount < 1 || args.Mode == ForeignTitleSourceMode.NodeOnly && manifest.NodeCount != 1)
                throw new CallFailed("source_changed");
            var facts = await ReadForeignPart(lease, opened.Handle, ForeignTitlePartKind.NodeFacts, manifest.NodeFacts, ct);
            var titles = await ReadForeignPart(lease, opened.Handle, ForeignTitlePartKind.Titles, manifest.Titles, ct);
            var nodes = ForeignTitlePayload.Decode(manifest.NodeCount, manifest.NodeFacts, manifest.Titles, facts, titles);
            if (nodes.Any(n => n.Machine != lease.Machine) || nodes[^1].SessionId != args.AnchorSessionId) throw new CallFailed("source_changed");
            return new(opened, Array.AsReadOnly(nodes));
        }
        catch { await lease.AbortAsync().ConfigureAwait(false); throw; }
    }
    internal async Task<ForeignTitleSourceChecked> CheckForeignTitlesAsync(RemoteMachine.RelayBulkLease lease, ForeignTitleSourceOpened opened, CancellationToken ct)
    {
        var result = await TitleCall(lease, "foreign_title_source_check", new RelayHandleArgs(opened.Handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.ForeignTitleSourceChecked, ct);
        var manifest = opened.Manifest;
        if (result.SourceMachine != lease.Machine || result.SourceMachine != manifest.SourceMachine || result.AnchorSessionId != manifest.AnchorSessionId ||
            result.TargetMachine != manifest.TargetMachine || result.NodeCount != manifest.NodeCount || !RelayHash.IsSha(result.OwnershipSha256)) throw new CallFailed("source_changed");
        return result;
    }
    internal async Task CloseForeignTitlesAsync(RemoteMachine.RelayBulkLease lease, string handle, CancellationToken ct)
    {
        try { await TitleCall(lease, "foreign_title_source_close", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean, ct); }
        catch { await lease.AbortAsync().ConfigureAwait(false); }
    }
    private static async Task<byte[]> ReadForeignPart(RemoteMachine.RelayBulkLease lease, string handle, ForeignTitlePartKind kind,
        ForeignTitlePartManifest manifest, CancellationToken ct)
    {
        if (manifest.Utf8Bytes < 0 || !RelayHash.IsSha(manifest.Sha256)) throw new CallFailed("bad_parts");
        using var segments = new RelaySegments(new(manifest.Utf8Bytes, manifest.Sha256));
        while (segments.Length < manifest.Utf8Bytes)
        {
            var part = await TitleCall(lease, "foreign_title_source_part", new ForeignTitlePartArgs(handle, kind, segments.Length),
                EngineSocketJson.Default.ForeignTitlePartArgs, EngineSocketJson.Default.ForeignTitlePart, ct);
            if (part.Offset != segments.Length || part.Total != manifest.Utf8Bytes || part.Next != checked(part.Offset + part.Data.LongLength)) throw new CallFailed("bad_parts");
            segments.Append(part.Offset, part.Data, ct);
        }
        return segments.Complete(ct);
    }
    private static async Task<TResult> TitleCall<TArgs, TResult>(RemoteMachine.RelayBulkLease lease, string method, TArgs args,
        JsonTypeInfo<TArgs> input, JsonTypeInfo<TResult> output, CancellationToken ct)
    {
        var result = await lease.CallAsync(method, JsonSerializer.SerializeToElement(args, input), ct).ConfigureAwait(false);
        return result.Deserialize(output) ?? throw new IOException("Invalid foreign title result");
    }
}
