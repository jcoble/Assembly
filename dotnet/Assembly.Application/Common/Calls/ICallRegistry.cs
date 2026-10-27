using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Calls;

/// <summary>Where a feature lists its calls (plan 5.2). B5 adds topics, B6 saves.</summary>
public interface ICallRegistry
{
    void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler);

    // The async part runs first, with the call's token, for anything outside the database (upload bytes, files,
    // hashing, another machine). The function it returns is the database part, run once inside IAtomicWriter.Write.
    void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler);
}
