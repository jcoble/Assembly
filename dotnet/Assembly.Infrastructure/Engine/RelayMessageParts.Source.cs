using Assembly.Application.Common.Calls;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class RelayMessageParts
{
    internal sealed partial class Operation
    {
        internal RelaySourceOpened PublishSource(RelaySourceCapture capture)
        {
            Token.ThrowIfCancellationRequested();
            var candidate = new SourceStage(capture, RelayDigests.Describe(capture));
            var published = false;
            try
            {
                lock (_owner._gate)
                {
                    RequireLive();
                    if (_connection.Source is not null) throw new CallFailed("relay_busy");
                    _connection.Source = candidate; published = true;
                    return new(candidate.Handle, candidate.Source);
                }
            }
            finally { if (!published) candidate.Dispose(); }
        }
        private SourceStage Source(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                return _connection.Source is { } source && source.Handle == handle ? source : throw new CallFailed("bad_handle");
            }
        }
        internal async Task<RelaySourcePart> ReadSource(RelaySourcePartArgs args)
        {
            var source = Source(args.Handle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, source.Stop.Token);
            await source.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                Token.ThrowIfCancellationRequested();
                var bytes = args.Part switch
                {
                    RelayPartKind.Body => source.Body,
                    RelayPartKind.SenderLabel => source.Label,
                    _ => throw new CallFailed("bad_parts")
                };
                if (args.Offset < 0 || args.Offset > bytes.LongLength || args.Limit is < 1 or > RelaySegments.ChunkBytes)
                    throw new CallFailed("bad_parts");
                var count = (int)Math.Min(args.Limit, bytes.LongLength - args.Offset);
                linked.Token.ThrowIfCancellationRequested();
                var part = bytes.AsSpan(checked((int)args.Offset), count).ToArray();
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.Source, source)) throw new CallFailed("bad_handle");
                    return new(part, args.Offset, args.Offset + count, bytes.LongLength);
                }
            }
            finally { source.Access.Release(); }
        }
        internal RelaySourceDescriptor SourceDescriptor(string handle) => Source(handle).Source;
        internal void CloseSource(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                var source = Source(handle);
                _connection.Source = null;
                _owner.Retire(_connection, source, source.Stop);
            }
        }
    }
}
