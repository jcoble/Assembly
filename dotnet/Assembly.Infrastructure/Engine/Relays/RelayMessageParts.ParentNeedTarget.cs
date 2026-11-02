using Assembly.Application.Common.Calls;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class RelayMessageParts
{
    internal sealed partial class Operation
    {
        internal ParentNeedUploadOpened PublishNeedTarget(ParentNeedUploadArgs args)
        {
            if (!Enum.IsDefined(args.Kind) || args.Payload.Utf8Bytes < 0 || !RelayHash.IsSha(args.Payload.Sha256) ||
                (args.Kind == ParentNeedUploadKind.Prepare
                    ? string.IsNullOrWhiteSpace(args.IntentId) || args.IntentVersion is null or < 0
                    : args.IntentId is not null || args.IntentVersion is not null)) throw new CallFailed("bad_args");
            var candidate = new NeedTargetStage(args);
            var published = false;
            try
            {
                lock (_owner._gate)
                {
                    RequireLive();
                    if (_connection.Target is not null || _connection.ForeignTarget is not null ||
                        _connection.StartTarget is not null || _connection.NeedTarget is not null) throw new CallFailed("relay_busy");
                    _connection.NeedTarget = candidate; published = true;
                    return new(candidate.Handle);
                }
            }
            finally { if (!published) candidate.Dispose(); }
        }
        private NeedTargetStage NeedTarget(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive();
                return _connection.NeedTarget is { } stage && stage.Handle == handle ? stage : throw new CallFailed("bad_handle");
            }
        }
        internal async Task<ParentNeedUploadOffset> AppendNeedTarget(ParentNeedUploadPartArgs args)
        {
            var stage = NeedTarget(args.Handle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, stage.Stop.Token);
            await stage.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                stage.Payload.Append(args.Offset, args.Data, linked.Token);
                lock (_owner._gate)
                {
                    RequireLive(); linked.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_connection.NeedTarget, stage)) throw new CallFailed("bad_handle");
                    return new(stage.Payload.Length);
                }
            }
            finally { stage.Access.Release(); }
        }
        internal async Task<(ParentNeedUploadArgs Args, byte[] Bytes)> AdmitNeedTarget(string handle, ParentNeedUploadKind kind)
        {
            var stage = NeedTarget(handle);
            if (stage.Args.Kind != kind) throw new CallFailed("bad_handle");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, stage.Stop.Token);
            await stage.Access.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var bytes = stage.Payload.Complete(linked.Token);
                lock (_owner._gate)
                {
                    RequireLive();
                    if (!ReferenceEquals(_connection.NeedTarget, stage)) throw new CallFailed("bad_handle");
                    _connection.NeedTarget = null;
                    _owner.Retire(_connection, stage, stage.Stop);
                    return (stage.Args, bytes);
                }
            }
            finally { stage.Access.Release(); }
        }
        internal void DropNeedTarget(string handle)
        {
            lock (_owner._gate)
            {
                RequireLive(); var stage = NeedTarget(handle); _connection.NeedTarget = null;
                _owner.Retire(_connection, stage, stage.Stop);
            }
        }
    }
}
