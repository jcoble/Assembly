using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitCheckoutScopeTests
{
    // Test-only interception of registered reads; all authority/IO remains in the real feature and dispatcher.
    private sealed class ReadRegistry(ICallRegistry inner, Fixture fixture, bool oldOwner = false) : ICallRegistry
    {
        public CallKind? KindOf(string feature, string method) => inner.KindOf(feature, method);
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false)
        {
            // A real older-owner registry genuinely lacks the new call.
            if (oldOwner && feature == "git" && method == "checkouts") return;
            inner.Read(feature, method, args, result, async (value, call) =>
            {
                var answer = await handler(value, call);
                if (call.SourceGuard is { } guard) { var lease = new GuardLease(guard); fixture.Captured = lease; call.SourceGuard = lease; }
                return answer;
            }, bulk);
        }
        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, handler, order, failed, bulk);
        public void Save<T>(string feature, JsonTypeInfo<T> type, Action<string, T, IWriteScope> save) => inner.Save(feature, type, save);
        public void Closed(Action<string> closed) => inner.Closed(closed);
        public void Topic<T>(string topic, JsonTypeInfo<T> type, Func<T, string> key, Func<string?, CallContext, IReadOnlyList<T>> state, bool terminal = false) => inner.Topic(topic, type, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
