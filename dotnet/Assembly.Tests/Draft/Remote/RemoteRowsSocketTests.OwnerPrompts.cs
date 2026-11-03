using Assembly.Application.Common;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Mcp;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Remote;

public sealed partial class RemoteRowsSocketTests
{
    private EngineRuntime _ownerEngine = null!;
    private NeedAnswers _ownerAnswers = null!;
    private SessionRegistry _ownerHosts = null!;
    private BackgroundWork _ownerWork = null!;

    private EngineDoor OwnerDoor(SessionStore sessions, MachineSet machines, global::Assembly.Infrastructure.Topics.Topics topics)
    {
        var launcher = new FakeLauncher();
        var limits = new SessionLimits(() => 5, () => 10);
        _ownerWork = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        _ownerHosts = new SessionRegistry(_remoteHub, sessions, launcher, TimeProvider.System, TextWriter.Null, _ownerWork);
        _ownerEngine = EngineRuntime.Start(_remoteDb, topics, TimeProvider.System, limits,
            cutover: new SessionCutover(_remoteDb, sessions, launcher, "checkouts", TimeProvider.System, owningHome: _owningHome));
        _ownerAnswers = new NeedAnswers(new McpGate(_ownerEngine, limits, TimeProvider.System),
            _ownerEngine, _ownerHosts, TimeProvider.System);
        return new EngineDoor(_ownerEngine, sessions, machines, new AttachmentStore(_remoteFile.Path + ".attachments"),
            TimeProvider.System, _ownerAnswers);
    }
}
