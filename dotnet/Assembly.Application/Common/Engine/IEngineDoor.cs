using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Engine;

public interface IEngineDoor
{
    Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(
        OwnerPromptRequest request, CancellationToken cancellationToken);
}
