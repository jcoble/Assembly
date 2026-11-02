using Assembly.Application.Common.Calls;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed partial class Operation
    {
        internal ParentNeedInputOpened PublishNeedInput(ParentNeedSourceCapture capture)
        {
            var candidate = new NeedSourceStage(capture);
            var manifest = new RelayPartManifest(candidate.Bytes.LongLength, RelayHash.Sha(candidate.Bytes));
            var published = false;
            try
            {
                lock (_owner._gate)
                {
                    RequireLive();
                    if (_connection.Source is not null || _connection.ForeignSource is not null || _connection.NeedSource is not null)
                        throw new CallFailed("relay_busy");
                    _connection.NeedSource = candidate; published = true;
                    return new(candidate.Handle, capture.Version, capture.State, capture.CreatedMs,
                        manifest);
                }
            }
            finally { if (!published) candidate.Dispose(); }
        }
        private NeedSourceStage NeedInput(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                return _connection.NeedSource is { } stage && stage.Handle == handle ? stage : throw new CallFailed("bad_handle");
            }
        }
        internal async Task<RelaySourcePart> ReadNeedInput(ParentNeedInputPartArgs args)
        {
            var stage = NeedInput(args.Handle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, stage.Stop.Token);
            await stage.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (args.Offset != stage.Next || args.Offset > stage.Bytes.LongLength || args.Limit is < 1 or > RelaySegments.ChunkBytes)
                    throw new CallFailed("bad_parts");
                var count = (int)Math.Min(args.Limit, stage.Bytes.LongLength - args.Offset);
                var bytes = stage.Bytes.AsSpan(checked((int)args.Offset), count).ToArray();
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.NeedSource, stage)) throw new CallFailed("bad_handle");
                    stage.Next += count;
                    return new(bytes, args.Offset, stage.Next, stage.Bytes.LongLength);
                }
            }
            finally { stage.Access.Release(); }
        }
        internal void CloseNeedInput(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive(); var stage = NeedInput(handle); _connection.NeedSource = null;
                _owner.Retire(_connection, stage, stage.Stop);
            }
        }
    }
}
