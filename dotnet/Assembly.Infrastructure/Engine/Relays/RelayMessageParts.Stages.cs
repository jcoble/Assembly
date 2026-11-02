using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed class SourceStage(RelaySourceCapture capture, RelaySourceDescriptor source) : IDisposable
    {
        internal readonly string Handle = Guid.NewGuid().ToString("N");
        internal readonly RelaySourceCapture Capture = capture;
        internal readonly RelaySourceDescriptor Source = source;
        internal readonly byte[] Body = RelayHash.Utf8.GetBytes(capture.Identity.Command.Text);
        internal readonly byte[] Label = RelayHash.Utf8.GetBytes(capture.SenderLabel);
        internal readonly SemaphoreSlim Access = new(1);
        internal readonly CancellationTokenSource Stop = new();
        public void Dispose() { Access.Dispose(); Stop.Dispose(); }
    }
    internal sealed class TargetStage(RelaySendRequest request) : IDisposable
    {
        internal readonly string Handle = Guid.NewGuid().ToString("N");
        internal readonly RelaySendRequest Request = request;
        internal readonly RelaySegments Body = new(request.Source.Body);
        internal readonly RelaySegments Label = new(request.Source.SenderLabel);
        internal readonly SemaphoreSlim Access = new(1);
        internal readonly CancellationTokenSource Stop = new();
        public void Dispose() { Body.Dispose(); Label.Dispose(); Access.Dispose(); Stop.Dispose(); }
    }
}
