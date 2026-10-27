using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Calls;

/// <summary>Where a feature lists its calls and topics (plan 5.2). B6 adds saves.</summary>
public interface ICallRegistry
{
    void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler);

    // The async part runs first, with the call's token, for anything outside the database (upload bytes, files,
    // hashing, another machine). The function it returns is the database part, run once inside IAtomicWriter.Write.
    void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler);

    // A topic's state for a new subscriber, with its key. Synchronous: Topics reads it under its lock, which can't span
    // an await (5.7). Its token is the socket's closing. Every write's publish waits on that lock, so state only reads
    // and stays small: a write inside it would take the locks in the writer's opposite order and deadlock under load,
    // and a big state stalls every write while it runs.
    void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<string?, CallContext, IReadOnlyList<TItem>> state);
}
