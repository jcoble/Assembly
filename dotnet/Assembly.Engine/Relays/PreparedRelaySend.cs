using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Relays;

/// <summary>Only verified complete bytes can construct the nonwire fresh-send preparation.</summary>
internal sealed class PreparedRelaySend
{
    private readonly byte[] _body, _label;
    internal RelaySourceDescriptor Source { get; }
    internal SendMessage Command { get; }
    internal string Canonical { get; }
    private PreparedRelaySend(RelaySourceDescriptor source, SendMessage command, byte[] body, byte[] label)
    { Source = source; Command = command; _body = body; _label = label; Canonical = RelayDigests.Canonical(source); }

    internal static PreparedRelaySend Create(RelaySourceDescriptor source, ReadOnlySpan<byte> body, ReadOnlySpan<byte> label)
    {
        RelayDigests.Validate(source);
        if (body.Length != source.Body.Utf8Bytes || label.Length != source.SenderLabel.Utf8Bytes ||
            RelayHash.Sha(body) != source.Body.Sha256 || RelayHash.Sha(label) != source.SenderLabel.Sha256)
            throw new RelayRefused("source_changed");
        var bodyOwned = body.ToArray(); var labelOwned = label.ToArray();
        var text = RelayHash.Utf8.GetString(bodyOwned); var capturedLabel = RelayHash.Utf8.GetString(labelOwned);
        if (source.SenderKind == "system" && capturedLabel != "Assembly") throw new RelayRefused("bad_args");
        var command = new SendMessage(source.FromSessionId, source.ToSessionId, text, source.Mode);
        var capture = RelayDigests.Capture(source.FromMachine, source.RecipientMachine, source.MessageId, source.SenderKind, command, capturedLabel, source.Fence, source.ReplyEvidence);
        if (RelayDigests.Describe(capture) != source) throw new RelayRefused("source_changed");
        return new(source, command, bodyOwned, labelOwned);
    }

    internal void AssertReplyBindings(CommandContext context, SendMessage command, byte[]? body, byte[]? label, string? kind)
    {
        if (Source.ReplyEvidence is null || !ReferenceEquals(command, Command) || !ReferenceEquals(body, _body) || !ReferenceEquals(label, _label) ||
            context.EntityId != Source.MessageId || context.Machine != Source.RecipientMachine || kind != "session" ||
            context.Actor != new EngineActor(ActorKind.System, "machine:" + Source.FromMachine)) throw new RelayRefused("forbidden");
    }

    internal bool HasReplyParent(IReadScope scope, string machine)
    {
        var chain = SessionAncestry.Read(scope, machine, Source.FromSessionId!);
        return chain.Known && chain.Nodes.Count > 1 && chain.Nodes[0].Id == Source.FromSessionId &&
            chain.Nodes[0].ParentId == Source.ToSessionId && chain.Nodes[0].Machine == Source.FromMachine &&
            chain.Nodes[1].Id == Source.ToSessionId && chain.Nodes[1].Machine == machine && chain.Nodes[1].State != "foreign";
    }

    internal CommandResult Apply(IWriteScope scope, CommandContext context, int messageLimit, MessageExpiryPolicy policy)
    {
        if (Source.SenderKind == "system" && Source.MessageId.StartsWith("need/", StringComparison.Ordinal) && !HasReplyParent(scope, context.Machine))
            return new(false, false, "forbidden", context.ExpectedVersion, 0, context.Epoch);
        return MessageTransitions.ApplyRelaySend(scope, context, Command, _body, _label, Source.SenderKind, messageLimit, policy, Source.ReplyEvidence is null ? null : this);
    }
}
