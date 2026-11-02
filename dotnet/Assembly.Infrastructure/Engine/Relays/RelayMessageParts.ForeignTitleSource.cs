using Assembly.Application.Common.Calls;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed partial class Operation
    {
        internal ForeignTitleSourceOpened PublishForeignSource(ForeignTitleSourceCapture capture)
        {
            Token.ThrowIfCancellationRequested(); var candidate = new ForeignSourceStage(capture); var published = false;
            try
            {
                lock (_owner._gate)
                {
                    RequireLive();
                    if (_connection.Source is not null || _connection.ForeignSource is not null) throw new CallFailed("relay_busy");
                    _connection.ForeignSource = candidate; published = true;
                    return new(candidate.Handle, capture.Manifest);
                }
            }
            finally { if (!published) candidate.Dispose(); }
        }
        private ForeignSourceStage ForeignSource(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                return _connection.ForeignSource is { } source && source.Handle == handle ? source : throw new CallFailed("bad_handle");
            }
        }
        internal ForeignTitleSourceCapture ForeignSourceCapture(string handle) => ForeignSource(handle).Capture;
        internal async Task<ForeignTitlePart> ReadForeignSource(ForeignTitlePartArgs args)
        {
            var source = ForeignSource(args.Handle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, source.Stop.Token);
            await source.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var bytes = args.Part switch
                {
                    ForeignTitlePartKind.NodeFacts => source.Capture.NodeFacts,
                    ForeignTitlePartKind.Titles => source.Capture.Titles,
                    _ => throw new CallFailed("bad_parts")
                };
                if (args.Offset < 0 || args.Offset > bytes.LongLength || args.Limit is < 1 or > RelaySegments.ChunkBytes) throw new CallFailed("bad_parts");
                var count = (int)Math.Min(args.Limit, bytes.LongLength - args.Offset);
                linked.Token.ThrowIfCancellationRequested();
                var part = bytes.AsSpan(checked((int)args.Offset), count).ToArray();
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.ForeignSource, source)) throw new CallFailed("bad_handle");
                    return new(part, args.Offset, args.Offset + count, bytes.LongLength);
                }
            }
            finally { source.Access.Release(); }
        }
        internal bool CloseForeignSource(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                if (_connection.ForeignSource is not { } source || source.Handle != handle) return false;
                _connection.ForeignSource = null; _owner.Retire(_connection, source, source.Stop); return true;
            }
        }
    }
}
