using Assembly.Application.Common.Calls;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed class StartTargetStage(StartTitleBeginArgs request) : IDisposable
    {
        internal readonly string Handle = Guid.NewGuid().ToString("N");
        internal readonly StartTitleBeginArgs Request = StartTitlePayload.Freeze(request);
        internal readonly RelaySegments Facts = new(new(request.Facts.Utf8Bytes, request.Facts.Sha256));
        internal readonly RelaySegments Title = new(new(request.Title.Utf8Bytes, request.Title.Sha256));
        internal readonly SemaphoreSlim Access = new(1);
        internal readonly CancellationTokenSource Stop = new();
        public void Dispose() { Facts.Dispose(); Title.Dispose(); Access.Dispose(); Stop.Dispose(); }
    }
    internal sealed partial class Operation
    {
        internal string PublishStartTarget(StartTitleBeginArgs request)
        {
            Token.ThrowIfCancellationRequested(); var candidate = new StartTargetStage(request); var published = false;
            try
            {
                lock (_owner._gate)
                {
                    RequireLive();
                    if (_connection.Target is not null || _connection.ForeignTarget is not null || _connection.StartTarget is not null)
                        throw new CallFailed("relay_busy");
                    _connection.StartTarget = candidate; published = true; return candidate.Handle;
                }
            }
            finally { if (!published) candidate.Dispose(); }
        }
        private StartTargetStage StartTarget(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                return _connection.StartTarget is { } target && target.Handle == handle ? target : throw new CallFailed("bad_handle");
            }
        }
        internal async Task<StartTitleOffsets> AppendStartTitle(StartTitlePartArgs args)
        {
            var target = StartTarget(args.Handle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, target.Stop.Token);
            await target.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var part = args.Part switch
                {
                    StartTitlePartKind.Facts => target.Facts,
                    StartTitlePartKind.Title => target.Title,
                    _ => throw new CallFailed("bad_parts")
                };
                part.Append(args.Offset, args.Data, linked.Token);
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.StartTarget, target)) throw new CallFailed("bad_handle");
                    return new(target.Facts.Length, target.Title.Length);
                }
            }
            finally { target.Access.Release(); }
        }
        internal async Task<Assembly.Engine.Sessions.PreparedSessionTitleCommand> AdmitStartTitle(StartTitleCommitArgs args, string machine)
        {
            var target = StartTarget(args.Handle ?? throw new CallFailed("bad_handle"));
            if (target.Request != args.Request) throw new CallFailed("source_changed");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, target.Stop.Token);
            await target.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var prepared = StartTitlePayload.Prepare(machine, target.Request, target.Facts.Complete(linked.Token), target.Title.Complete(linked.Token));
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.StartTarget, target)) throw new CallFailed("bad_handle");
                    _connection.StartTarget = null; _owner.Retire(_connection, target, target.Stop); return prepared;
                }
            }
            finally { target.Access.Release(); }
        }
        internal bool DropStartTarget(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                if (_connection.StartTarget is not { } target || target.Handle != handle) return false;
                _connection.StartTarget = null; _owner.Retire(_connection, target, target.Stop); return true;
            }
        }
    }
}
