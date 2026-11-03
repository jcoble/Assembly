using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Engine;

public interface IEngineDoor
{
    Task<Func<IWriteScope, bool>> PrepareNeedAnswerAsync(NeedAnswerRequest request, CancellationToken cancellationToken);

    Task<SessionTitleStat> ReadSessionTitleAsync(SessionTitleReadArgs request, CancellationToken cancellationToken);
    Task<SessionTitleChunk> ReadSessionTitleChunkAsync(SessionTitleChunkArgs request, CancellationToken cancellationToken);

    Task<OwnerPromptPage> ReadOwnerPromptsAsync(OwnerPromptReadArgs request, CancellationToken cancellationToken);
    Task<OwnerPromptChunk> ReadOwnerPromptChunkAsync(OwnerPromptChunkArgs request, CancellationToken cancellationToken);

    Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(
        OwnerPromptRequest request, CancellationToken cancellationToken);
}
