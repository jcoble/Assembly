using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    // A controlled transport boundary around real owning Engine writes. No provider result is invented.
    private async Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareTransportPrompt(OwnerPromptRequest request, CancellationToken ct)
    {
        var session = _sessions.Get(request.Session) ?? throw new CallFailed("unknown session");
        var machine = session.Machine is null ? _here : _box;
        var owner = session.Machine is null ? _sessions : _remoteSessions;
        var runtime = session.Machine is null ? _engine : _remoteRuntime;
        if (session.Machine is not null && owner.Get(session.SessionId) is null)
        {
            _remoteOwner.Db.Writer.Write("test.remote-existing-session", scope =>
            {
                SessionImport.Import(scope, runtime.Machine, 1,
                    [new(session.SessionId, null, session.Agent, null, _remoteHome, "Remote", "")]);
                owner.ProjectEngineSession(scope, new(session.SessionId, session.Agent, _remoteHome, "Remote", null, 1), null, _remoteHome);
                return 0;
            });
        }
        var door = new EngineDoor(runtime, owner, new MachineSet(machine), _attachments, _time, null!);
        var apply = await door.PrepareOwnerPromptAsync(request, ct);
        if (session.Machine is null)
        {
            await machine.OwnerPromptAsync(request.Session, request.Text, () => true, ct);
            return scope => machine.Refuses ? throw new CallFailed("refused") : apply(scope);
        }
        var accepted = await machine.OwnerPromptAsync(request.Session, request.Text,
            () => _remoteOwner.Db.Writer.Write("send/send", apply), ct);
        return _ => accepted;
    }
}
