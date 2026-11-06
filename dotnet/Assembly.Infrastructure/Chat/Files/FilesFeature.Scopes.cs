using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Infrastructure.Chat.Files;

internal sealed partial class FilesFeature
{
    private FileRelayScopes relayScopes = null!;

    private void RegisterScopes(ICallRegistry calls)
    {
        relayScopes = new FileRelayScopes(connection =>
        {
            reads.Close(connection);
            uploads.Close(connection);
            observations.Close(connection);
            searches.Close(connection);
            backgroundWork.Start("file relay search cleanup", _ => searches.CloseAsync(connection));
        });
        calls.Write(Feature, "openScope", FilesJson.Default.FileScopeOpen, FilesJson.Default.FileScopeGrant,
            (_, call) =>
            {
                var grant = relayScopes.Open(call);
                return Task.FromResult<Func<IWriteScope, FileScopeGrant>>(_ => grant);
            }, failed: (_, call) => relayScopes.FailedOpen(call));
        calls.Read(Feature, "attachScope", FilesJson.Default.FileScopeAttach, FilesJson.Default.UnobservedRoot,
            (args, call) =>
            {
                relayScopes.Attach(args.Scope, args.Attachment, call);
                return Task.FromResult(new UnobservedRoot(true));
            }, bulk: true);
        calls.Read(Feature, "closeScope", FilesJson.Default.FileScopeClose, FilesJson.Default.UnobservedRoot,
            (args, call) =>
            {
                if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
                return Task.FromResult(new UnobservedRoot(relayScopes.Release(args.Scope, call.Connection)));
            });
        calls.Closed(relayScopes.Close);
    }

    private FileRelayScopes.BoundCall? Bind<TArgs>(TArgs args, JsonTypeInfo<TArgs> type, CallContext call)
    {
        var json = JsonSerializer.SerializeToElement(args, type);
        if (!json.TryGetProperty("relayScope", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } scope)
            throw new CallBadArguments("Invalid file relay scope.");
        return relayScopes.Bind(scope, call);
    }

    // Only these existing Files calls gain a resource scope; dispatcher ordering and bulk admission stay in one place.
    private sealed class ScopedRegistry(FilesFeature owner, ICallRegistry inner) : ICallRegistry
    {
        public CallKind? KindOf(string feature, string method) => inner.KindOf(feature, method);

        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false)
        {
            if (feature != Feature) throw new InvalidOperationException("Expected Files registration.");
            inner.Read(feature, method, args, result, async (value, call) =>
            {
                using var source = await owner.CaptureSource(method, value, args, call);
                var admitted = source?.Context ?? call;
                if (owner.remoteRoutes is not null)
                {
                    var forwarded = await owner.remoteRoutes.Invoke(method, value, args, result, admitted, bulk, write: false);
                    if (forwarded.Forwarded) { source?.Confirm(write: false); return forwarded.Result; }
                }
                using var bound = owner.Bind(value, args, admitted);
                var answer = await handler(value, bound?.Context ?? admitted);
                source?.Confirm(write: false);
                return answer;
            }, bulk);
        }

        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false)
        {
            if (feature != Feature) throw new InvalidOperationException("Expected Files registration.");
            inner.Write(feature, method, args, result, async (value, call) =>
            {
                using var source = await owner.CaptureSource(method, value, args, call);
                var admitted = source?.Context ?? call;
                if (owner.remoteRoutes is not null)
                {
                    var forwarded = await owner.remoteRoutes.Invoke(method, value, args, result, admitted, bulk, write: true);
                    if (forwarded.Forwarded) { source?.Confirm(write: true); return _ => forwarded.Result; }
                }
                owner.RequireViewWritable(value, args, call);
                using var bound = owner.Bind(value, args, admitted);
                var apply = await handler(value, bound?.Context ?? admitted);
                source?.Confirm(write: true);
                return apply;
            }, value => owner.remoteRoutes?.Order(value, args) ?? order?.Invoke(value), failed is null ? null : (value, call) =>
            {
                FileRelayScopes.BoundCall? bound;
                try { bound = owner.Bind(value, args, call with { Cancellation = CancellationToken.None, Closed = CancellationToken.None }); }
                catch (CallFailed) { return; } // Closing the scope already closed all of its resources.
                using (bound) failed(value, bound?.Context ?? call);
            }, bulk);
        }

        public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
            Func<string?, CallContext, IReadOnlyList<TItem>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened, IRowSourceBindings sources) => inner.Rows(topic, state, opened, sources);
    }
}
