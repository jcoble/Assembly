using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Infrastructure.Remote;

// Holds only the selected registered read. The real handler, dispatcher, machine transport and token remain in use.
internal sealed class MessageReadGate(ICallRegistry inner, Func<string, CallContext, Task> before, CallPeer? peer = null) : ICallRegistry
{
    public CallKind? KindOf(string feature, string method) => inner.KindOf(feature, method);
    public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<R>> handler, bool bulk = false) =>
        inner.Read(feature, method, args, result, async (value, call) => { await before(method, call); return await handler(value, peer is null ? call : call with { Peer = peer }); }, bulk);
    public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler, Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) =>
        inner.Write(feature, method, args, result, handler, order, failed, bulk);
    public void Save<T>(string feature, JsonTypeInfo<T> value, Action<string, T, IWriteScope> save) => inner.Save(feature, value, save);
    public void Closed(Action<string> connection) => inner.Closed(connection);
    public void Topic<T>(string topic, JsonTypeInfo<T> item, Func<T, string> key, Func<string?, CallContext, IReadOnlyList<T>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
    public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
}
