using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed partial class EngineDoor
{
    public async Task<SessionTitleStat> ReadSessionTitleAsync(SessionTitleReadArgs request, CancellationToken cancellationToken)
    {
        try
        {
            var remote = sessions.Get(request.Session)?.Machine is null ? null : ReadRemote(request.Session);
            if (remote is null) return runtime.Reader.SessionTitle(request, cancellationToken);
            return (await remote.ReadOwnerPromptAsync("session_title", JsonSerializer.SerializeToElement(request with { Machine = null },
                SessionsJson.Default.SessionTitleReadArgs), false, request.Machine, cancellationToken)).Deserialize(SessionsJson.Default.SessionTitleStat)!;
        }
        catch (Assembly.Application.Common.Machines.MachineUnreachable error) { throw new InvalidOperationException("title_changed", error); }
        catch (Assembly.Application.Common.Calls.CallFailed error) when (error.Message == "title_changed") { throw new InvalidOperationException("title_changed", error); }
    }
    public async Task<SessionTitleChunk> ReadSessionTitleChunkAsync(SessionTitleChunkArgs request, CancellationToken cancellationToken)
    {
        try
        {
            var remote = sessions.Get(request.Session)?.Machine is null ? null : ReadRemote(request.Session);
            if (remote is null) return runtime.Reader.SessionTitleChunk(request, cancellationToken);
            return (await remote.ReadOwnerPromptAsync("session_title_chunk", JsonSerializer.SerializeToElement(request with { Machine = null },
                SessionsJson.Default.SessionTitleChunkArgs), true, request.Machine, cancellationToken)).Deserialize(SessionsJson.Default.SessionTitleChunk)!;
        }
        catch (Assembly.Application.Common.Machines.MachineUnreachable error) { throw new InvalidOperationException("title_changed", error); }
        catch (Assembly.Application.Common.Calls.CallFailed error) when (error.Message == "title_changed") { throw new InvalidOperationException("title_changed", error); }
    }
}
