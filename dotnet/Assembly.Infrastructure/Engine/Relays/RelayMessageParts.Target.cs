using Assembly.Application.Common.Calls;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed partial class Operation
    {
        internal RelaySendBeginReply PublishTarget(RelaySendRequest request, RelaySendBeginReply probe)
        {
            if (probe.Status != "parts_required") return Return(probe);
            Token.ThrowIfCancellationRequested();
            var candidate = new TargetStage(request);
            var published = false;
            try
            {
                lock (_owner._gate)
                {
                    RequireLive();
                    if (_connection.NeedTarget is not null || _connection.Target is not null || _connection.ForeignTarget is not null || _connection.StartTarget is not null) throw new CallFailed("relay_busy");
                    _connection.Target = candidate; published = true;
                    return probe with { Transfer = candidate.Handle };
                }
            }
            finally { if (!published) candidate.Dispose(); }
        }
        private TargetStage Target(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                return _connection.Target is { } target && target.Handle == handle ? target : throw new CallFailed("bad_handle");
            }
        }
        internal async Task<RelaySendPartAccepted> Append(RelaySendPartArgs args)
        {
            var target = Target(args.Transfer);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, target.Stop.Token);
            await target.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var part = args.Part switch
                {
                    RelayPartKind.Body => target.Body,
                    RelayPartKind.SenderLabel => target.Label,
                    _ => throw new CallFailed("bad_parts")
                };
                part.Append(args.Offset, args.Data, linked.Token);
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.Target, target)) throw new CallFailed("bad_handle");
                    return new(target.Body.Length, target.Label.Length);
                }
            }
            finally { target.Access.Release(); }
        }
        internal async Task<PreparedRelaySend> Admit(RelaySendCommitArgs args)
        {
            var target = Target(args.Transfer ?? throw new CallFailed("bad_handle"));
            if (target.Request != args.Request) throw new CallFailed("source_changed");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, target.Stop.Token);
            await target.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var prepared = PreparedRelaySend.Create(args.Request.Source, target.Body.Complete(linked.Token), target.Label.Complete(linked.Token));
                // Once admitted, immutable preparation belongs to the returned writer, never connection cleanup.
                lock (_owner._gate)
                {
                    RequireLive();
                    if (!ReferenceEquals(_connection.Target, target)) throw new CallFailed("bad_handle");
                    _connection.Target = null;
                    _owner.Retire(_connection, target, target.Stop);
                    return prepared;
                }
            }
            finally { target.Access.Release(); }
        }
        internal void DropTarget(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive(); var target = Target(handle); _connection.Target = null;
                _owner.Retire(_connection, target, target.Stop);
            }
        }
    }
}
