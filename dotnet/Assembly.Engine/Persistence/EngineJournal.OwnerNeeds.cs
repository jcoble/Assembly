using System.Security.Cryptography;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static (PreparedTextJson Canonical, string Fingerprint) PrepareOwnerNeedCanonical(
        PreparedNeedCommand.OwnerIdentity identity, ResolveNeed original, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(FingerprintPrefix(OwnerContext(identity, 0)));
        var canonical = PreparedTextJson.CreateOwnerNeed(original, hash.AppendData, ct);
        return (canonical, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static CommandResult ExecuteOwnerNeed(IWriteScope scope, PreparedNeedCommand prepared, long nowMs,
        MessageExpiryPolicy expiryPolicy)
    {
        var owner = prepared.Owner ?? throw new ArgumentException("Owner need preparation is required.", nameof(prepared));
        var context = OwnerContext(owner.Identity, nowMs);
        var admission = new OwnerNeedAdmission(prepared);
        return ExecuteCore(scope, context, context.CommandId.ToString("N"), prepared.CanonicalJournal,
            () => NeedTransitions.ApplyOwner(scope, context, prepared, expiryPolicy), ownerNeedAdmission: admission);
    }

    private static CommandContext OwnerContext(PreparedNeedCommand.OwnerIdentity identity, long nowMs) =>
        new(identity.CommandId, identity.Machine, new(ActorKind.Window, "owner"), identity.NeedId,
            identity.Version, identity.Epoch, nowMs);

    private sealed class OwnerNeedAdmission(PreparedNeedCommand prepared)
    {
        internal string OriginalFingerprint => prepared.Owner!.OriginalFingerprint;
        internal void Validate(CommandContext context, string key, string canonical)
        {
            var identity = prepared.Owner!.Identity;
            if (prepared.Command is not ResolveNeed || context.CommandId != identity.CommandId ||
                context.Machine != identity.Machine || context.EntityId != identity.NeedId ||
                context.ExpectedVersion != identity.Version || context.Epoch != identity.Epoch ||
                context.Actor is not { Kind: ActorKind.Window, Id: "owner" } ||
                key != identity.CommandId.ToString("N") || canonical != prepared.CanonicalJournal)
                throw new ArgumentException("Owner need admission does not match its preparation.");
        }
    }
}
