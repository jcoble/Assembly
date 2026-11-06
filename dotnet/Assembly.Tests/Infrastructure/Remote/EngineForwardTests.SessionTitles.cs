using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_title_normal_or_bulk_read_never_returns_bytes_from_a_replaced_same_identity_connection(bool bulk)
    {
        await using var h = await Started();
        var f = h.Remote.Fixture;
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "test"), "s", f.Number("SELECT version FROM engine_session WHERE id='s'"), 0, 1000);
        Assert.True(f.Db.Writer.Write("title", s => h.Remote.Runtime.Commands.ApplyPreparedSessionTitle(s, context,
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(new string('x', 40000) + "\0suffix")))).Applied);
        var stat = (await h.ConnectedMachine.ReadOwnerPromptAsync("session_title", JsonSerializer.SerializeToElement(new SessionTitleReadArgs("s"),
            EngineSocketJson.Default.SessionTitleReadArgs), false, null, default)).Deserialize(EngineSocketJson.Default.SessionTitleStat)!;
        Assert.Equal(f.Machine, stat.EngineMachine);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OwnerReading = (_, _) => { entered.TrySetResult(); return release.Task; };
        var args = bulk ? JsonSerializer.SerializeToElement(new SessionTitleChunkArgs("s", stat.Version, stat.Epoch, stat.Utf8Bytes, 0), EngineSocketJson.Default.SessionTitleChunkArgs)
            : JsonSerializer.SerializeToElement(new SessionTitleReadArgs("s"), EngineSocketJson.Default.SessionTitleReadArgs);
        var pending = h.ConnectedMachine.ReadOwnerPromptAsync(bulk ? "session_title_chunk" : "session_title", args, bulk, f.Machine, default);
        try
        {
            await entered.Task.WaitAsync(Wait);
            h.Remote.Endpoint.DropAll(); h.Remote.Commit(1); await h.Reached(h.Remote.Through);
            release.TrySetResult();
            await Assert.ThrowsAsync<MachineUnreachable>(() => pending.WaitAsync(Wait));
        }
        finally { release.TrySetResult(); }
    }
}
