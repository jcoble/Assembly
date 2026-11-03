using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;

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

    Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareSourceOwnerPromptAsync(OwnerPromptRequest request, CallContext call) =>
        request.ExpectedSource is null && call.ExpectedSource is null ? PrepareOwnerPromptAsync(request, call.Cancellation) : throw Unavailable();
    Task<Func<IWriteScope, bool>> PrepareSourceNeedAnswerAsync(NeedAnswerRequest request, CallContext call) =>
        request.ExpectedSource is null && call.ExpectedSource is null ? PrepareNeedAnswerAsync(request, call.Cancellation) : throw Unavailable();
    Task<Func<IWriteScope, bool>> PrepareStopAsync(string session, string run, ExpectedSource source, CallContext call) => throw Unavailable();
    Task<Func<IWriteScope, bool>> PrepareStopTaskAsync(string session, string task, ExpectedSource source, CallContext call) => throw Unavailable();
    Task<SessionConfig> ReadSessionConfigAsync(string session, ExpectedSource source, CallContext call) => throw Unavailable();
    Task<SessionConfig> WarmSessionConfigAsync(string session, string target, ExpectedSource source, CallContext call) => throw Unavailable();
    Task<Func<IWriteScope, SessionConfig>> PrepareSessionConfigAsync(string session, string config, string value,
        string? target, ExpectedSource source, CallContext call) => throw Unavailable();
    Task<OwnerPromptPage> ReadSourceOwnerPromptsAsync(OwnerPromptReadArgs request, CallContext call) => throw Unavailable();
    Task<OwnerPromptChunk> ReadSourceOwnerPromptChunkAsync(OwnerPromptChunkArgs request, CallContext call) => throw Unavailable();

    private static CallFailed Unavailable() => new("Source-bound chat commands are unavailable.", "source_changed");
}
