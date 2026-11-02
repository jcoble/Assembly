using Assembly.Engine.Relays;

namespace Assembly.Engine.Sessions;

internal static class ForeignTitleDigests
{
    internal static string Structure(string target, string source, string parent, IReadOnlyList<ForeignAncestor> nodes)
    {
        using var hash = new RelayHash();
        hash.String("foreign-title-structure/v1"); hash.String(target); hash.String(source); hash.String(parent); hash.Length(nodes.Count);
        foreach (var node in nodes) { hash.String(node.SessionId); hash.String(node.Machine); hash.NullableString(node.ParentId); }
        return hash.Finish();
    }

    internal static string Stored(string target, IReadOnlyList<ForeignAncestor> nodes)
    {
        using var hash = new RelayHash();
        hash.String("foreign-title-stored-snapshot/v1"); hash.String(target); hash.Length(nodes.Count);
        foreach (var node in nodes)
        {
            hash.String(node.SessionId); hash.String(node.Machine); hash.String(node.Provider); hash.NullableString(node.ParentId);
            hash.String(node.Title);
        }
        return hash.Finish();
    }
}
