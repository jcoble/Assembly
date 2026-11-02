using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Engine;

public interface IEngineDoor
{
    Task<OwnerPromptPage> ReadOwnerPromptsAsync(OwnerPromptReadArgs request, CancellationToken cancellationToken);
    Task<OwnerPromptChunk> ReadOwnerPromptChunkAsync(OwnerPromptChunkArgs request, CancellationToken cancellationToken);

    Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(
        OwnerPromptRequest request, CancellationToken cancellationToken);
}
