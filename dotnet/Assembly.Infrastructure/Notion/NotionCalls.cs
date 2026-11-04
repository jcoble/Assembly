using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Notion;

namespace Assembly.Infrastructure.Notion;

/// <summary>Desktop-only routes. The writer supplied by the socket owns each persisted transition.</summary>
public sealed class NotionCalls : IFeature, IAsyncDisposable
{
    private readonly NotionSettings _settings;
    private readonly NotionOAuth _oauth;
    private readonly NotionTaskExports _exports;
    private readonly NotionDetailPages _detail;
    private readonly NotionCache _cache;
    private readonly NotionRefresh _refresh;
    private readonly NotionEdits _edits;
    private readonly NotionImageUpload _images;
    private readonly ConcurrentDictionary<string, IDisposable> _pending = new();
    private readonly Channel<bool> _cleanup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _worker;
    public NotionCalls(NotionSettings settings, NotionOAuth oauth, INotionDetailReader reader, NotionCache cache, NotionRefresh refresh, NotionEdits edits, NotionImageUpload images)
    {
        _settings = settings; _oauth = oauth; _cache = cache; _refresh = refresh; _edits = edits; _images = images;
        _exports = new(settings, reader); _detail = new(settings, reader);
        _worker = CleanupAsync();
        _cleanup.Writer.TryWrite(true);
    }
    private async Task CleanupAsync()
    {
        await foreach (var _ in _cleanup.Reader.ReadAllAsync())
            await _settings.ReconcileAsync(CancellationToken.None);
    }
    public void Register(ICallRegistry calls)
    {
        var j = NotionJson.Default;
        Write(calls, "notion", "imageBegin", j.NotionImageBegin, j.NotionImageLease, async (args, call) =>
        { var lease = await _images.BeginAsync(call.Connection, args.Source, args.Mime, args.Size, call.Cancellation); return _ => lease; }, bulk: true);
        Write(calls, "notion", "imageChunk", j.NotionImageChunk, j.NotionEmpty, async (args, call) =>
        { var result = await _images.ChunkAsync(call.Connection, args.UploadId, args.Offset, args.Data, call.Cancellation); return _ => result; }, bulk: true);
        Write(calls, "notion", "imageCommit", j.NotionImageOperation, j.NotionEditReceipt, async (args, call) =>
        { var receipt = await _images.CommitAsync(call.Connection, args.UploadId, call.Cancellation); return _ => receipt; }, bulk: true);
        Write(calls, "notion", "imageDrop", j.NotionImageOperation, j.NotionEmpty, (args, call) =>
        { _images.Drop(call.Connection, args.UploadId); return Task.FromResult<Func<IWriteScope, NotionEmpty>>(_ => new()); }, bulk: true);
        calls.Closed(_images.Close);
        Write(calls, "notion", "property", j.NotionPropertyArgs, j.NotionEditReceipt, async (args, call) =>
        {
            var pending = await _edits.PreparePropertyAsync(args.Source, args.Property, args.Value, args.ObservedRevision, call.Cancellation);
            return pending.Commit;
        });
        Write(calls, "notion", "block", j.NotionBlockArgs, j.NotionEditReceipt, async (args, call) =>
        {
            var receipt = await _edits.BlockAsync(args.Source, args.BlockId, args.Kind, args.Text, args.Checked, args.ObservedRevision, call.Cancellation);
            return _ => receipt;
        });
        Write(calls, "notion", "appendText", j.NotionAppendArgs, j.NotionEditReceipt, async (args, call) =>
        {
            var receipt = await _edits.AppendTextAsync(args.Source, args.Markdown, call.Cancellation);
            return _ => receipt;
        });
        Topic(calls, "notion-tasks", j.NotionRevision, _ => "cache", (_, call) => { var page = _cache.Query(new(Limit: 1), call.Cancellation); return [new(page.Generation, page.Revision)]; });
        Read(calls, "notion", "list", j.NotionQuery, j.NotionTaskPage, (args, call) => Task.FromResult(_cache.Query(args, call.Cancellation)));
        Read(calls, "notion", "view", j.NotionEmpty, j.NotionQuery, (_, _) => Task.FromResult(_cache.View()));
        Write(calls, "notion", "saveView", j.NotionQuery, j.NotionQuery, (args, _) => Task.FromResult<Func<IWriteScope, NotionQuery>>(scope => _cache.SaveView(scope, args)));
        Write(calls, "notion", "refresh", j.NotionRefreshArgs, j.NotionRefreshReceipt, async (args, call) =>
        {
            var fetched = await _refresh.PrepareAsync(args.ExpectedGeneration, call.Cancellation);
            return scope => _cache.Replace(scope, args.ExpectedGeneration, fetched.Rows, fetched.FetchedMs, fetched.Truncated);
        });
        Read(calls, "notion", "export", j.NotionTaskRef, j.NotionTaskExport, (args, call) => _exports.ExportAsync(args, call.Cancellation), bulk: true);
        Read(calls, "notion", "detail", j.NotionDetailArgs, j.DetailPage, (args, call) => _detail.PageAsync(call.Connection, args, call.Cancellation), bulk: true);
        calls.Closed(_detail.Close);
        Topic(calls, "notion-settings", j.NotionSettingsView, _ => "connection", (_, _) => [_settings.Read()]);
        Read(calls, "notion", "settings", j.NotionEmpty, j.NotionSettingsView, (_, _) => Task.FromResult(_settings.Read()));
        Write(calls, "notion", "saveSettings", j.NotionSaveSettings, j.NotionSettingsView, async (args, call) =>
        {
            var staged = await _settings.StageAsync(args.DataSourceId, args.Token, call.Cancellation);
            Hold(call.Connection, staged);
            return scope => Finish(scope, call.Connection, () => staged.Commit(scope));
        }, failed: (_, call) => Release(call.Connection));
        Write(calls, "notion", "clearSettings", j.NotionEmpty, j.NotionSettingsView, async (_, call) =>
        {
            Hold(call.Connection, await _settings.LeaseAsync(call.Cancellation));
            return scope => Finish(scope, call.Connection, () => _settings.Clear(scope));
        }, failed: (_, call) => Release(call.Connection));
        Write(calls, "notion", "oauthBegin", j.NotionEmpty, j.NotionOAuthStart, async (_, call) =>
        {
            var start = await _oauth.BeginAsync(call.Cancellation);
            return _ => start;
        });
        Write(calls, "notion", "oauthClaim", j.NotionClaim, j.NotionSettingsView, async (args, call) =>
        {
            var staged = await _oauth.StageClaimAsync(args.OperationId, args.State, args.ClaimCode, call.Cancellation);
            Hold(call.Connection, staged);
            return scope => Finish(scope, call.Connection, () => staged.Commit(scope));
        }, failed: (_, call) => Release(call.Connection));
        Write(calls, "notion", "oauthCancel", j.NotionOperation, j.NotionEmpty, (args, _) =>
        {
            _oauth.Cancel(args.OperationId);
            return Task.FromResult<Func<IWriteScope, NotionEmpty>>(_ => new());
        });
        calls.Closed(Release);
    }
    private void Hold(string connection, IDisposable pending)
    {
        if (!_pending.TryAdd(connection, pending)) { pending.Dispose(); throw new NotionException("notion_busy"); }
    }
    private T Finish<T>(IWriteScope scope, string connection, Func<T> commit)
    {
        var result = commit();
        scope.AfterCommit(() => Release(connection));
        return result;
    }
    private void Release(string connection)
    {
        if (_pending.TryRemove(connection, out var pending)) pending.Dispose();
        _cleanup.Writer.TryWrite(true);
    }
    public async ValueTask DisposeAsync()
    {
        await _images.DisposeAsync();
        await _oauth.DisposeAsync();
        foreach (var connection in _pending.Keys) Release(connection);
        _cleanup.Writer.TryComplete();
        await _worker;
    }

    private static void Owner(CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Notion is desktop-only", "forbidden");
    }
    private static void Read<TArgs, TResult>(ICallRegistry calls, string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) => calls.Read(feature, method, args, result, async (value, call) =>
        {
            Owner(call);
            try { return await handler(value, call); }
            catch (NotionException e) { throw new CallFailed(e.Code, e.Code); }
        }, bulk);
    private static void Write<TArgs, TResult>(ICallRegistry calls, string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Action<TArgs, CallContext>? failed = null, bool bulk = false) => calls.Write(feature, method, args, result, async (value, call) =>
        {
            Owner(call);
            try
            {
                var commit = await handler(value, call);
                return scope => { try { return commit(scope); } catch (NotionException e) { throw new CallFailed(e.Code, e.Code); } };
            }
            catch (NotionException e) { throw new CallFailed(e.Code, e.Code); }
        }, failed: failed, bulk: bulk);
    private static void Topic<T>(ICallRegistry calls, string topic, JsonTypeInfo<T> type, Func<T, string> key, Func<string?, CallContext, IReadOnlyList<T>> state) =>
        calls.Topic(topic, type, key, (value, call) => { Owner(call); return state(value, call); });
}
