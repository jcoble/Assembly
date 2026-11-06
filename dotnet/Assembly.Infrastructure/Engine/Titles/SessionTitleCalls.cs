using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Hosting;

namespace Assembly.Infrastructure.Engine.Titles;

/// <summary>Fixed title reads shared by window commands and authenticated peer publication.</summary>
internal sealed class SessionTitleCalls(EngineRuntime runtime, SessionStore sessions, MachineForward? forward = null) : IFeature
{
    private MachineForward Forward => forward ?? throw new KeyNotFoundException("Unknown remote engine machine");
    private bool IsRemote(string? machine) => machine is not null && machine != runtime.Machine;

    public void Register(ICallRegistry calls)
    {
        calls.Read("engine", "session_title", EngineSocketJson.Default.SessionTitleReadArgs, EngineSocketJson.Default.SessionTitleStat,
            async (args, call) => IsRemote(args.Machine)
                ? (await Forward.CallAsync(args.Machine!, "engine", "session_title",
                    JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.SessionTitleReadArgs), false, call.Cancellation)).Deserialize(EngineSocketJson.Default.SessionTitleStat)!
                : TitleRead(() => runtime.Reader.SessionTitle(args, call.Cancellation)));
        calls.Read("engine", "session_title_chunk", EngineSocketJson.Default.SessionTitleChunkArgs, EngineSocketJson.Default.SessionTitleChunk,
            async (args, call) => IsRemote(args.Machine)
                ? (await Forward.CallAsync(args.Machine!, "engine", "session_title_chunk",
                    JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.SessionTitleChunkArgs), true, call.Cancellation)).Deserialize(EngineSocketJson.Default.SessionTitleChunk)!
                : TitleRead(() => runtime.Reader.SessionTitleChunk(args, call.Cancellation)), bulk: true);
        calls.Read("engine", "legacy_session_title", EngineSocketJson.Default.LegacySessionTitleStatArgs, EngineSocketJson.Default.LegacySessionTitleStat,
            (args, call) => Task.FromResult(ReadLegacy(call, scope =>
            {
                var stat = sessions.LegacyTitleStat(scope, runtime.Machine, args);
                var owning = scope.QueryFirst("""
                    SELECT e.version,e.epoch FROM engine_session e JOIN session_info s ON s.session_id=e.id
                    WHERE e.id=$id AND e.machine=$machine AND e.holder='local' AND e.state<>'foreign'
                      AND CAST(e.title AS BLOB)=CAST(s.title AS BLOB)
                    """, row => new { Version = row.Long(0), Epoch = row.Long(1) },
                    new SqlArg("$id", args.Reference.SessionId), new SqlArg("$machine", runtime.Machine));
                return stat with { OwningEngineVersion = owning?.Version, OwningEngineEpoch = owning?.Epoch };
            })));
        calls.Read("engine", "legacy_session_title_chunk", EngineSocketJson.Default.LegacySessionTitleChunkArgs, EngineSocketJson.Default.LegacySessionTitleChunk,
            (args, call) => Task.FromResult(ReadLegacy(call, scope => sessions.LegacyTitleChunk(scope, runtime.Machine, args))), bulk: true);
    }

    private T ReadLegacy<T>(CallContext call, Func<IReadScope, T> read)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
        return TitleRead(() => ((IEngineRuntime)runtime).Read(scope =>
        {
            if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) != runtime.Machine)
                throw new InvalidOperationException("title_changed");
            return read(scope);
        }, call.Cancellation));
    }

    private static T TitleRead<T>(Func<T> read)
    {
        try { return read(); }
        catch (InvalidOperationException error) when (error.Message == "title_changed") { throw new CallFailed(error.Message); }
    }
}
