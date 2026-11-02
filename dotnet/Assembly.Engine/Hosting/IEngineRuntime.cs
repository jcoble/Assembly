using Assembly.Application.Common.Database;

namespace Assembly.Engine.Hosting;

/// <summary>Trusted host code only; never a serialized or caller-selected API. Callbacks return materialized values synchronously.</summary>
public interface IEngineRuntime
{
    string Machine { get; }
    EngineCommands Commands { get; }
    T Write<T>(Func<IWriteScope, T> body);
    T Read<T>(Func<IReadScope, T> body, CancellationToken cancellationToken);
}
