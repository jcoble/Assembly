using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Calls;

/// <summary>Where a feature lists its calls, saves and topics (plan 5.2).</summary>
public interface ICallRegistry
{
    void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler);

    // The async part runs first, with the call's token, for anything outside the database (upload bytes, files,
    // hashing, another machine). The function it returns is the database part, run once inside IAtomicWriter.Write.
    void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler);

    // A save (5.9): `{feature}/set` with {key, value}, one write each, audited as "{feature}/set <key>", never with its
    // value. Its database part publishes the change on the topic named after its feature, which must be registered first.
    void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save);

    // A topic's state for a new subscriber, with its key. Synchronous: Topics reads it under its lock, which can't span
    // an await (5.7). Its token is the socket's closing. Every write's publish waits on that lock, so state only reads
    // and stays small: a write inside it would take the locks in the writer's opposite order and deadlock under load,
    // and a big state stalls every write while it runs. key gives each item's key, which its state frame carries, so
    // the page can hold a key with a save in flight.
    void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key, Func<string?, CallContext, IReadOnlyList<TItem>> state);

    // A topic of rows in seq order (5.1, 5.7), keyed by session, which a sub must name. Its state gets the key and the
    // sub's `after` and gives the seq the rows after it continue from, then those rows as JSON; the state frame carries
    // that seq, each row goes out as a `rows` frame, and so does every row published on the topic after.
    void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state);
}
