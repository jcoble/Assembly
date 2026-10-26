namespace Assembly.Engine.Database;

/// <summary>
/// The one way to write: each write is one transaction, committed whole or not at all. Synchronous on purpose: the
/// body can't await, so no transaction is held open across an agent call, a network call or a wait.
/// </summary>
public interface IAtomicWriter
{
    T Write<T>(string what, Func<IWriteScope, T> write);
}
