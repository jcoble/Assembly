using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal abstract class NeedAnswerStage : IDisposable
    {
        internal readonly string Handle = Guid.NewGuid().ToString("N");
        internal readonly SemaphoreSlim Access = new(1);
        internal readonly CancellationTokenSource Stop = new();
        public virtual void Dispose() { Access.Dispose(); Stop.Dispose(); }
    }
    internal sealed class NeedSourceStage(ParentNeedSourceCapture capture) : NeedAnswerStage
    {
        internal readonly ParentNeedSourceCapture Capture = capture;
        internal readonly byte[] Bytes = capture.Bytes();
        internal long Next;
    }
    internal sealed class NeedTargetStage(ParentNeedUploadArgs args) : NeedAnswerStage
    {
        internal readonly ParentNeedUploadArgs Args = args;
        internal readonly RelaySegments Payload = new(args.Payload);
        public override void Dispose() { Payload.Dispose(); base.Dispose(); }
    }
}
