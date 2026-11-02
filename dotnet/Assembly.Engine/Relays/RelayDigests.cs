using System.Buffers;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Relays;

internal static class RelayDigests
{
    internal static RelaySourceCapture Capture(string source, string target, string messageId, string senderKind,
        SendMessage command, string label, RelaySourceFence fence)
    {
        ValidateScalars(source, target, messageId, command.FromSessionId, senderKind, command.ToSessionId, command.Mode);
        var body = RelayHash.Utf8.GetBytes(command.Text);
        var labelBytes = RelayHash.Utf8.GetBytes(label);
        var canonical = PreparedTextJson.Create(command with { Text = "" }, "text", command.Text);
        var identity = Identity(source, target, messageId, senderKind, command, canonical.Utf8Bytes);
        var content = Content(source, target, messageId, senderKind, command, body, labelBytes);
        return new(new(source, messageId, senderKind, command), target, label, fence, content, identity);
    }

    internal static RelaySourceDescriptor Describe(RelaySourceCapture capture)
    {
        var identity = capture.Identity;
        var command = identity.Command;
        ValidateScalars(identity.FromMachine, capture.RecipientMachine, identity.MessageId, command.FromSessionId,
            identity.SenderKind, command.ToSessionId, command.Mode);
        var body = RelayHash.Utf8.GetBytes(command.Text);
        var label = RelayHash.Utf8.GetBytes(capture.SenderLabel);
        var canonical = PreparedTextJson.Create(command with { Text = "" }, "text", command.Text);
        if (Identity(identity.FromMachine, capture.RecipientMachine, identity.MessageId, identity.SenderKind, command, canonical.Utf8Bytes) != capture.IdentitySha256 ||
            Content(identity.FromMachine, capture.RecipientMachine, identity.MessageId, identity.SenderKind, command, body, label) != capture.SourceSha256)
            throw new RelayRefused("source_changed");
        return new(identity.FromMachine, capture.RecipientMachine, identity.MessageId, command.FromSessionId,
            identity.SenderKind, command.ToSessionId, command.Mode, capture.Fence, capture.IdentitySha256, capture.SourceSha256,
            canonical.Sha256, canonical.Utf8Bytes, new(body.LongLength, RelayHash.Sha(body)), new(label.LongLength, RelayHash.Sha(label)));
    }

    private static string Identity(string source, string target, string messageId, string senderKind, SendMessage command, long length)
    {
        using var hash = new RelayHash();
        hash.String("relay-send-identity/v1"); hash.String(target); hash.String(source); hash.String(messageId); hash.String(senderKind);
        hash.Length(length);
        PreparedTextJson.VisitCanonical(command with { Text = "" }, "text", command.Text, hash.Raw);
        return hash.Finish();
    }

    private static string Content(string source, string target, string messageId, string kind, SendMessage command,
        ReadOnlySpan<byte> body, ReadOnlySpan<byte> label)
    {
        using var hash = new RelayHash();
        hash.String("relay-source-content/v1"); hash.String(source); hash.String(target); hash.String(messageId);
        hash.NullableString(command.FromSessionId); hash.String(kind); hash.String(command.ToSessionId); hash.String(command.Mode);
        hash.Flag(true); hash.Bytes(body); hash.Bytes(label);
        return hash.Finish();
    }

    internal static string DescriptorCommitment(RelaySourceDescriptor source)
    {
        ValidateManifest(source);
        using var hash = new RelayHash();
        hash.String("relay-send-descriptor/v1"); hash.String(source.FromMachine); hash.String(source.RecipientMachine);
        hash.String(source.MessageId); hash.NullableString(source.FromSessionId); hash.String(source.SenderKind);
        hash.String(source.ToSessionId); hash.String(source.Mode); hash.String(source.IdentitySha256);
        hash.String(source.SourceSha256); hash.String(source.CommandSha256); hash.Length(source.CommandUtf8Bytes);
        hash.Length(source.Body.Utf8Bytes); hash.String(source.Body.Sha256);
        hash.Length(source.SenderLabel.Utf8Bytes); hash.String(source.SenderLabel.Sha256);
        return hash.Finish();
    }

    internal static string Canonical(RelaySourceDescriptor source)
    {
        Validate(source);
        var commitment = DescriptorCommitment(source);
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteString("kind", "send_message"); json.WriteNumber("relayJournalVersion", 1);
            json.WriteString("identitySha256", source.IdentitySha256); json.WriteString("sourceSha256", source.SourceSha256);
            json.WriteString("descriptorSha256", commitment); json.WriteString("commandSha256", source.CommandSha256);
            json.WriteNumber("commandUtf8Bytes", source.CommandUtf8Bytes); json.WriteNumber("bodyUtf8Bytes", source.Body.Utf8Bytes);
            json.WriteString("senderLabelSha256", source.SenderLabel.Sha256); json.WriteNumber("senderLabelUtf8Bytes", source.SenderLabel.Utf8Bytes);
            json.WriteEndObject();
        }
        if (buffer.WrittenCount > 32 * 1024) throw new RelayRefused("bad_args");
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static void Validate(RelaySourceDescriptor source)
    {
        ValidateScalars(source.FromMachine, source.RecipientMachine, source.MessageId, source.FromSessionId,
            source.SenderKind, source.ToSessionId, source.Mode);
        ValidateManifest(source);
        var fence = source.Fence;
        if (fence is null || fence.MessageVersion < 1 || fence.Attempt < 0 || fence.SenderEpoch < 0 || fence.RecipientStubEpoch < 0 ||
            fence.SenderReservedEpoch < 0 || string.IsNullOrWhiteSpace(fence.SenderHolder) || string.IsNullOrWhiteSpace(fence.RecipientHolder) ||
            fence.State is not ("queued" or "held" or "delivered_to_turn" or "next_turn" or "unsupported" or "failed"))
            throw new RelayRefused("bad_args");
    }

    private static void ValidateManifest(RelaySourceDescriptor source)
    {
        if (!RelayHash.IsSha(source.IdentitySha256) || !RelayHash.IsSha(source.SourceSha256) || !RelayHash.IsSha(source.CommandSha256) ||
            source.CommandUtf8Bytes < 0 || source.Body is null || source.SenderLabel is null ||
            source.Body.Utf8Bytes < 0 || source.SenderLabel.Utf8Bytes < 0 || !RelayHash.IsSha(source.Body.Sha256) || !RelayHash.IsSha(source.SenderLabel.Sha256))
            throw new RelayRefused("bad_args");
    }

    private static void ValidateScalars(string source, string target, string messageId, string? from, string kind, string to, string mode)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || source == target || string.IsNullOrWhiteSpace(messageId) ||
            string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || mode is not ("prompt" or "interrupt") || kind is not ("session" or "system") ||
            kind == "system" && (messageId != "child-ended/" + from || mode != "prompt")) throw new RelayRefused("bad_args");
    }

    internal static bool CanSend(RelaySourceDescriptor source) => source.Fence is
    {
        State: "queued", InFlight: false,
        SenderHolder: "local", SenderReservedEpoch: null
    } && source.Fence.RecipientHolder == source.RecipientMachine;
}
