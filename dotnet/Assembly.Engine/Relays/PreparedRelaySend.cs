using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;

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
        var capture = RelayDigests.Capture(source.FromMachine, source.RecipientMachine, source.MessageId, source.SenderKind, command, capturedLabel, source.Fence);
        if (RelayDigests.Describe(capture) != source) throw new RelayRefused("source_changed");
        return new(source, command, bodyOwned, labelOwned);
    }

    internal CommandResult Apply(IWriteScope scope, CommandContext context, int messageLimit, MessageExpiryPolicy policy) =>
        MessageTransitions.ApplyRelaySend(scope, context, Command, _body, _label, Source.SenderKind, messageLimit, policy);
}
