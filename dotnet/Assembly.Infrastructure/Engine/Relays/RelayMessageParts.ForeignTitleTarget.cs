using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed partial class Operation
    {
        internal ForeignTitleReply PublishForeignTarget(ForeignTitleRequest request, ForeignTitleReply probe)
        {
            if (probe.Kind != ForeignTitleReplyKind.PartsRequired) return Return(probe);
            Token.ThrowIfCancellationRequested(); var candidate = new ForeignTargetStage(request); var published = false;
            try
            {
                lock (_owner._gate)
                {
                    RequireLive();
                    if (_connection.Target is not null || _connection.ForeignTarget is not null || _connection.StartTarget is not null) throw new CallFailed("relay_busy");
                    _connection.ForeignTarget = candidate; published = true; return probe with { Handle = candidate.Handle };
                }
            }
            finally { if (!published) candidate.Dispose(); }
        }
        private ForeignTargetStage ForeignTarget(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                return _connection.ForeignTarget is { } target && target.Handle == handle ? target : throw new CallFailed("bad_handle");
            }
        }
        internal async Task<ForeignTitleOffsets> AppendForeignTitle(ForeignTitleAppendArgs args)
        {
            var target = ForeignTarget(args.Handle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, target.Stop.Token);
            await target.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var part = args.Part switch
                {
                    ForeignTitlePartKind.NodeFacts => target.NodeFacts,
                    ForeignTitlePartKind.Titles => target.Titles,
                    _ => throw new CallFailed("bad_parts")
                };
                part.Append(args.Offset, args.Data, linked.Token);
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.ForeignTarget, target)) throw new CallFailed("bad_handle");
                    return new(target.NodeFacts.Length, target.Titles.Length);
                }
            }
            finally { target.Access.Release(); }
        }
        internal async Task<ForeignTitlePayload> AdmitForeignTitle(ForeignTitleCommitArgs args, EngineCommands receiving, string machine)
        {
            var target = ForeignTarget(args.Handle ?? throw new CallFailed("bad_handle"));
            if (target.Request != args.Request) throw new CallFailed("source_changed");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, target.Stop.Token);
            await target.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var prepared = ForeignTitlePayload.Prepare(receiving, machine, target.Request, target.NodeFacts.Complete(linked.Token), target.Titles.Complete(linked.Token));
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.ForeignTarget, target)) throw new CallFailed("bad_handle");
                    _connection.ForeignTarget = null; _owner.Retire(_connection, target, target.Stop); return prepared;
                }
            }
            finally { target.Access.Release(); }
        }
        internal bool DropForeignTarget(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                if (_connection.ForeignTarget is not { } target || target.Handle != handle) return false;
                _connection.ForeignTarget = null; _owner.Retire(_connection, target, target.Stop); return true;
            }
        }
    }
}
