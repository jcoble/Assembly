using System.Text;
using System.Text.Json;
using Assembly.Engine.Persistence;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Sessions;

/// <summary>Complete, closed title payload verification before the receiving writer is admitted.</summary>
internal sealed class ForeignTitlePayload
{
    internal PreparedForeignAncestryBatch? Batch { get; }
    internal PreparedSessionTitleCommand? Registration { get; }
    internal ForeignTitleRequest Request { get; }
    private ForeignTitlePayload(ForeignTitleRequest request, PreparedForeignAncestryBatch? batch, PreparedSessionTitleCommand? registration)
    { Request = request; Batch = batch; Registration = registration; }

    internal static (byte[] NodeFacts, byte[] Titles) Encode(IReadOnlyList<ForeignAncestor> nodes)
    {
        using var data = new MemoryStream();
        var facts = new ForeignTitleNode[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i]; var bytes = RelayHash.Utf8.GetBytes(node.Title);
            facts[i] = new(i, node.SessionId, node.Machine, node.Provider, node.ParentId, data.Length, bytes.LongLength, RelayHash.Sha(bytes));
            data.Write(bytes);
        }
        return (JsonSerializer.SerializeToUtf8Bytes(facts, ForeignTitleJson.Default.ForeignTitleNodeArray), data.ToArray());
    }

    internal static ForeignTitleRequest Freeze(ForeignTitleRequest request)
    {
        if (!Enum.IsDefined(request.Operation) || string.IsNullOrWhiteSpace(request.TargetMachine) || string.IsNullOrWhiteSpace(request.FromMachine) ||
            request.TargetMachine == request.FromMachine || string.IsNullOrWhiteSpace(request.EntityId) || request.NodeCount < 1 ||
            request.NodeFacts is null || request.Titles is null || request.NodeFacts.Utf8Bytes < 0 || request.Titles.Utf8Bytes < 0 ||
            !RelayHash.IsSha(request.NodeFacts.Sha256) || !RelayHash.IsSha(request.Titles.Sha256)) throw Refused();
        if (request.Operation == ForeignTitleOperation.RegisterForeign)
        {
            var query = request.Registration;
            if (request.Ancestry is not null || request.NodeCount != 1 || query is null || query.Canonical is null ||
                query.FromMachine != request.FromMachine || query.EntityId != request.EntityId || query.CommandId == Guid.Empty ||
                query.ExpectedVersion < 0 || query.Epoch < 0) throw Refused();
            _ = EngineJournal.ForeignRegisterCanonicalJson(query);
            return request with { Registration = query with { Canonical = query.Canonical with { } }, NodeFacts = request.NodeFacts with { }, Titles = request.Titles with { } };
        }
        if (request.Registration is not null || request.Ancestry is not { } ancestry || ancestry.FromMachine != request.FromMachine ||
            ancestry.ParentSessionId != request.EntityId || ancestry.NodeCount != request.NodeCount || !RelayHash.IsSha(ancestry.StructureSha256)) throw Refused();
        return request with { Ancestry = ancestry with { }, NodeFacts = request.NodeFacts with { }, Titles = request.Titles with { } };
    }

    internal static ForeignTitlePayload Prepare(EngineCommands receiving, string actualTarget, ForeignTitleRequest request, byte[] nodeFacts, byte[] titles)
    {
        var frozen = Freeze(request);
        if (frozen.TargetMachine != actualTarget) throw Refused("wrong_machine");
        var nodes = Decode(frozen.NodeCount, frozen.NodeFacts, frozen.Titles, nodeFacts, titles);
        if (nodes[^1].SessionId != frozen.EntityId || nodes[^1].Machine != frozen.FromMachine) throw Refused();
        if (frozen.Operation == ForeignTitleOperation.RegisterForeign)
        {
            var node = nodes[0];
            var prepared = PreparedSessionTitleCommand.Prepare(new RegisterForeignSession(node.Machine, node.Provider, node.Title));
            if (prepared.CanonicalJournal != EngineJournal.ForeignRegisterCanonicalJson(frozen.Registration!)) throw Refused();
            return new(frozen, null, prepared);
        }
        for (var i = 0; i < nodes.Length; i++)
            if (nodes[i].ParentId != (i == 0 ? null : nodes[i - 1].SessionId)) throw Refused();
        if (ForeignTitleDigests.Structure(actualTarget, frozen.FromMachine, frozen.EntityId, nodes) != frozen.Ancestry!.StructureSha256) throw Refused();
        var batch = receiving.PrepareForeignAncestry(new(frozen.FromMachine, frozen.EntityId, nodes));
        if (batch.TargetMachine != actualTarget) throw Refused("wrong_machine");
        return new(frozen, batch, null);
    }

    internal static ForeignAncestor[] Decode(long count, ForeignTitlePartManifest factsManifest, ForeignTitlePartManifest titlesManifest,
        byte[] nodeFacts, byte[] titles)
    {
        if (count < 1 || nodeFacts.LongLength != factsManifest.Utf8Bytes || titles.LongLength != titlesManifest.Utf8Bytes ||
            RelayHash.Sha(nodeFacts) != factsManifest.Sha256 || RelayHash.Sha(titles) != titlesManifest.Sha256) throw Refused();
        try
        {
            var facts = JsonSerializer.Deserialize(nodeFacts, ForeignTitleJson.Default.ForeignTitleNodeArray);
            if (facts is null || facts.LongLength != count || !nodeFacts.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(facts, ForeignTitleJson.Default.ForeignTitleNodeArray)))
                throw Refused();
            var nodes = new ForeignAncestor[facts.Length]; var ids = new HashSet<string>(StringComparer.Ordinal); long offset = 0;
            for (var i = 0; i < facts.Length; i++)
            {
                var fact = facts[i];
                if (fact is null || fact.Index != i || string.IsNullOrWhiteSpace(fact.SessionId) || !ids.Add(fact.SessionId) ||
                    string.IsNullOrWhiteSpace(fact.Machine) || string.IsNullOrWhiteSpace(fact.Provider) || fact.TitleOffset != offset ||
                    fact.TitleUtf8Bytes < 0 || fact.TitleUtf8Bytes > titles.LongLength - offset || !RelayHash.IsSha(fact.TitleSha256)) throw Refused();
                var bytes = titles.AsSpan(checked((int)offset), checked((int)fact.TitleUtf8Bytes));
                if (RelayHash.Sha(bytes) != fact.TitleSha256) throw Refused();
                nodes[i] = new(fact.SessionId, fact.Machine, fact.Provider, RelayHash.Utf8.GetString(bytes), fact.ParentId);
                offset = checked(offset + fact.TitleUtf8Bytes);
            }
            if (offset != titles.LongLength) throw Refused();
            return nodes;
        }
        catch (JsonException) { throw Refused(); }
        catch (DecoderFallbackException) { throw Refused(); }
        catch (OverflowException) { throw Refused(); }
    }
    private static ForeignAncestryRefused Refused(string error = "bad_parts") => new(new(false, false, error, 0, 0, 0));
}
