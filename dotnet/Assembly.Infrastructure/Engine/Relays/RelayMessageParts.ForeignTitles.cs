using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed class ForeignSourceStage(ForeignTitleSourceCapture capture) : IDisposable
    {
        internal readonly string Handle = Guid.NewGuid().ToString("N");
        internal readonly ForeignTitleSourceCapture Capture = capture;
        internal readonly SemaphoreSlim Access = new(1);
        internal readonly CancellationTokenSource Stop = new();
        public void Dispose() { Access.Dispose(); Stop.Dispose(); }
    }
    internal sealed class ForeignTargetStage(ForeignTitleRequest request) : IDisposable
    {
        internal readonly string Handle = Guid.NewGuid().ToString("N");
        internal readonly ForeignTitleRequest Request = ForeignTitlePayload.Freeze(request);
        internal readonly RelaySegments NodeFacts = new(new(request.NodeFacts.Utf8Bytes, request.NodeFacts.Sha256));
        internal readonly RelaySegments Titles = new(new(request.Titles.Utf8Bytes, request.Titles.Sha256));
        internal readonly SemaphoreSlim Access = new(1);
        internal readonly CancellationTokenSource Stop = new();
        public void Dispose() { NodeFacts.Dispose(); Titles.Dispose(); Access.Dispose(); Stop.Dispose(); }
    }
}
