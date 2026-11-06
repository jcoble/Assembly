using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Sessions;

internal sealed record ForeignTitleOwnership(string Id, string Machine, string Provider, string? ParentId,
    long Epoch, string Holder, long? ReservedEpoch);

/// <summary>Actual owning snapshot; titles stay frozen while current ownership can be revalidated.</summary>
internal sealed class ForeignTitleSourceCapture
{
    internal ForeignTitleSourceManifest Manifest { get; }
    internal IReadOnlyList<ForeignAncestor> Nodes { get; }
    internal IReadOnlyList<ForeignTitleOwnership> Ownership { get; }
    internal byte[] NodeFacts { get; }
    internal byte[] Titles { get; }
    internal ForeignTitleSourceCapture(ForeignTitleSourceOpenArgs args, string machine,
        ForeignAncestor[] nodes, byte[][] titles, ForeignTitleOwnership[] ownership)
    {
        Nodes = Array.AsReadOnly(nodes); Ownership = Array.AsReadOnly(ownership);
        using var data = new MemoryStream();
        var facts = new ForeignTitleNode[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i]; var bytes = titles[i];
            facts[i] = new(i, node.SessionId, node.Machine, node.Provider, node.ParentId, data.Length, bytes.LongLength, RelayHash.Sha(bytes));
            data.Write(bytes);
        }
        Titles = data.ToArray();
        NodeFacts = JsonSerializer.SerializeToUtf8Bytes(facts, ForeignTitleJson.Default.ForeignTitleNodeArray);
        Manifest = new(args.Mode, machine, args.AnchorSessionId, args.TargetMachine, nodes.LongLength,
            new(NodeFacts.LongLength, RelayHash.Sha(NodeFacts)), new(Titles.LongLength, RelayHash.Sha(Titles)));
    }

    internal ForeignTitleSourceChecked Checked()
    {
        using var hash = new RelayHash();
        hash.String("foreign-title-source-ownership/v1"); hash.Length((long)Manifest.Mode);
        hash.String(Manifest.SourceMachine); hash.String(Manifest.TargetMachine); hash.String(Manifest.AnchorSessionId);
        hash.Length(Ownership.Count);
        foreach (var node in Ownership)
        {
            hash.String(node.Id); hash.String(node.Machine); hash.String(node.Provider); hash.NullableString(node.ParentId);
            hash.Length(node.Epoch); hash.String(node.Holder); hash.Flag(node.ReservedEpoch is not null);
            if (node.ReservedEpoch is { } epoch) hash.Length(epoch);
        }
        return new(Manifest.SourceMachine, Manifest.AnchorSessionId, Manifest.TargetMachine, Manifest.NodeCount, hash.Finish());
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ForeignTitleNode[]))]
internal sealed partial class ForeignTitleJson : JsonSerializerContext;
