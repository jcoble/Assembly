using System.Text;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Sessions;

/// <summary>Frozen complete ancestry input bound to the actual receiving command instance.</summary>
public sealed class PreparedForeignAncestryBatch
{
    public string TargetMachine { get; }
    internal string FromMachine { get; }
    internal string ParentSessionId { get; }
    internal IReadOnlyList<ForeignAncestor> Nodes { get; }
    internal string ChainDigest { get; }
    private readonly byte[][] _titles;
    internal byte[] TitleUtf8(int index) => _titles[index];

    internal PreparedForeignAncestryBatch(string target, ForeignAncestryArgs args)
    {
        TargetMachine = target; FromMachine = args.FromMachine; ParentSessionId = args.ParentSessionId;
        if (args.RootToParent is not { Count: > 0 } || args.RootToParent.Any(n => n is null || n.Title is null))
            throw new ForeignAncestryRefused(new(false, false, "bad_args", 0, 0, 0));
        var nodes = args.RootToParent.Select(n => n with { }).ToArray();
        Nodes = Array.AsReadOnly(nodes);
        try
        {
            _titles = nodes.Select(n => new UTF8Encoding(false, true).GetBytes(n.Title)).ToArray();
            ChainDigest = RelayReceiptKeys.Digest(new[] { "foreign-parent-batch/v1", target, FromMachine, ParentSessionId }
                .Concat(nodes.SelectMany(n => new[] { n.SessionId, n.Machine, n.Provider, n.Title, n.ParentId })).ToArray());
        }
        catch (EncoderFallbackException)
        { throw new ForeignAncestryRefused(new(false, false, "bad_args", 0, 0, 0)); }
    }
}
