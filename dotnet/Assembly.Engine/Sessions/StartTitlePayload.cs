using System.Text.Json;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Sessions;

/// <summary>Closed original Start facts and full Title, verified and prepared before writer admission.</summary>
internal static class StartTitlePayload
{
    internal static byte[] Facts(StartSession start) => JsonSerializer.SerializeToUtf8Bytes(
        new StartTitleFacts(start.Provider, start.Model, start.Folder, start.ParentId, start.RunId, start.TaskUrl, start.RepoUrl, start.ManagedCheckout, start.ProjectId, start.Checkout),
        StartTitleJson.Default.StartTitleFacts);

    internal static StartTitleBeginArgs Freeze(StartTitleBeginArgs args)
    {
        if (args.StageVersion != 1 || string.IsNullOrWhiteSpace(args.TargetMachine) || args.Start is not { } start || start.CommandId == Guid.Empty ||
            !Guid.TryParse(start.EntityId, out _) || string.IsNullOrWhiteSpace(start.FromMachine) || start.FromMachine == args.TargetMachine ||
            start.ExpectedVersion < 0 || start.Epoch < 0 || args.Facts is null || args.Title is null ||
            args.Facts.Utf8Bytes < 0 || args.Title.Utf8Bytes < 0 || !RelayHash.IsSha(args.Facts.Sha256) || !RelayHash.IsSha(args.Title.Sha256))
            throw new ArgumentException("bad_parts");
        _ = MachineStartCanonical.Json(start.Canonical);
        var canonical = start.Canonical switch
        {
            StartCanonicalSmall small => (StartCanonical)(small with { }),
            StartCanonicalLarge large => large with { },
            _ => throw new ArgumentException("bad_parts")
        };
        return args with { Start = start with { Canonical = canonical }, Facts = args.Facts with { }, Title = args.Title with { } };
    }

    internal static PreparedSessionTitleCommand Prepare(string actualTarget, StartTitleBeginArgs request, byte[] facts, byte[] title)
    {
        var frozen = Freeze(request);
        if (frozen.TargetMachine != actualTarget) throw new ArgumentException("wrong_machine");
        if (facts.LongLength != frozen.Facts.Utf8Bytes || title.LongLength != frozen.Title.Utf8Bytes ||
            RelayHash.Sha(facts) != frozen.Facts.Sha256 || RelayHash.Sha(title) != frozen.Title.Sha256) throw new ArgumentException("bad_parts");
        var decoded = JsonSerializer.Deserialize(facts, StartTitleJson.Default.StartTitleFacts);
        if (decoded is null || !facts.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(decoded, StartTitleJson.Default.StartTitleFacts)))
            throw new ArgumentException("bad_parts");
        var command = new StartSession(decoded.Provider, decoded.Model, decoded.Folder, RelayHash.Utf8.GetString(title),
            decoded.ParentId, decoded.RunId, decoded.TaskUrl, decoded.RepoUrl, decoded.ManagedCheckout, decoded.ProjectId, decoded.Checkout);
        var prepared = EngineCommands.PrepareSessionTitle(command);
        if (prepared.CanonicalJournal != MachineStartCanonical.Json(frozen.Start.Canonical)) throw new ArgumentException("bad_parts");
        return prepared;
    }
}
