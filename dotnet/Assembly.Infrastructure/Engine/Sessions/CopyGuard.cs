using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Engine.Sessions;

/// <summary>
/// Whether a remote session's saved copy here is still the selected source's (TSK-1692). It needs no connection, live
/// mirror or generation: the copy was saved from that engine, the machine name still points at it, in any state but
/// retired, and nothing here says the session belongs elsewhere.
/// </summary>
internal static class CopyGuard
{
    /// <summary>Throws <c>not_saved</c> when there is no such copy, and <c>source_changed</c> when the session moved.</summary>
    public static void Check(IReadScope read, SessionStore sessions, ExpectedSource source, string session)
    {
        if (source.RouteMachine is not { Length: > 0 } route) throw Changed();
        if (sessions.Get(read, session) is not { } info || info.Machine != route ||
            read.QueryFirst("SELECT machine FROM engine_session WHERE id=$id", row => row.Text(0), new SqlArg("$id", session))
                is { } owner && owner != source.Machine)
            throw Changed();
        var saved = read.QueryFirst("""
            SELECT EXISTS(SELECT 1 FROM remote_copy WHERE session_id=$session AND machine=$machine)
              AND EXISTS(SELECT 1 FROM engine_mirror WHERE machine=$machine AND name=$name AND state<>'retired')
            """, row => row.Long(0) != 0, new SqlArg("$session", session), new("$machine", source.Machine), new("$name", route));
        if (!saved) throw NotSaved(route);
    }

    /// <summary>A remote session with no saved copy here, while its machine can't be reached.</summary>
    public static CallFailed NotSaved(string machine) => new($"Not saved on this machine. Connect {machine} to open it.", "not_saved");

    private static CallFailed Changed() => new("The selected row source changed.", "source_changed");
}
