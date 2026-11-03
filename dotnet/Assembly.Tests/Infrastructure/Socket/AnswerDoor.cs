using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

internal sealed class AnswerDoor : IEngineDoor
{
    public Func<NeedAnswerRequest, Task<Func<IWriteScope, bool>>> Answer { get; set; } = _ => Task.FromResult<Func<IWriteScope, bool>>(_ => true);
    public int Prepared { get; private set; }

    public Task<Func<IWriteScope, bool>> PrepareNeedAnswerAsync(NeedAnswerRequest request, CancellationToken cancellationToken)
    {
        Prepared++;
        return Answer(request);
    }

    public Task<SessionTitleStat> ReadSessionTitleAsync(SessionTitleReadArgs request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SessionTitleChunk> ReadSessionTitleChunkAsync(SessionTitleChunkArgs request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<OwnerPromptPage> ReadOwnerPromptsAsync(OwnerPromptReadArgs request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<OwnerPromptChunk> ReadOwnerPromptChunkAsync(OwnerPromptChunkArgs request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(OwnerPromptRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
