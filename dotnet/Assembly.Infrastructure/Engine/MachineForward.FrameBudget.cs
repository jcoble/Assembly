using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineForward
{
    private static bool FitsMachineFrame<T>(string method, T args, JsonTypeInfo<T> type)
    {
        try
        {
            using var stream = new FrameCounter();
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject(); json.WriteString("t", "call"); json.WriteNumber("id", long.MinValue);
            json.WriteString("feature", "engine"); json.WriteString("method", method); json.WritePropertyName("args");
            JsonSerializer.Serialize(json, args, type); json.WriteEndObject(); json.Flush(); return true;
        }
        catch (FrameOverBudget) { return false; }
    }
    private static CommandResult TitleResult(ForeignTitleReply reply, string? expectedStructure = null)
    {
        if (reply.Kind is ForeignTitleReplyKind.Applied or ForeignTitleReplyKind.RecordedCanonical && reply.Result is { } recorded)
            return recorded;
        if (reply.Kind == ForeignTitleReplyKind.ExistingStructure && expectedStructure is not null && reply.VerifiedStructureSha256 == expectedStructure &&
            reply.Result is { Applied: true, Late: false, Error: null, Version: 0, Epoch: 0, Seq: 0 } actualMarker)
            return actualMarker;
        throw new CallFailed("unknown");
    }
    private sealed class FrameOverBudget : Exception;
    private sealed class FrameCounter : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { _length = checked(_length + buffer.Length); if (_length > 4 * 1024 * 1024) throw new FrameOverBudget(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
