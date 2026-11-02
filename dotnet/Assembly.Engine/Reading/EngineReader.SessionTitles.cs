using Assembly.Application.Common.Engine;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Reading;

public sealed partial class EngineReader
{
    public SessionTitleStat SessionTitle(SessionTitleReadArgs args, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct);
        var result = SessionTitleContent.Stat(read, machine, args);
        ct.ThrowIfCancellationRequested(); return result;
    }
    public SessionTitleChunk SessionTitleChunk(SessionTitleChunkArgs args, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct);
        var result = SessionTitleContent.Chunk(read, machine, args);
        ct.ThrowIfCancellationRequested(); return result;
    }
}
