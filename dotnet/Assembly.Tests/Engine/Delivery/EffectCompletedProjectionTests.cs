using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Delivery;

public sealed class EffectCompletedProjectionTests
{
    [Fact]
    public async Task Worker_projection_captures_confirmed_session_state_in_the_completion_transaction()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession("child", "running");
        Assert.True(f.Apply(new StopSession(), "child").Applied);
        var committed = new TaskCompletionSource<(string Effect, string Session)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = new FakeEffects
        {
            Execute = (_, _) => Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new SessionEffectData("idle", null, null), EngineJson.Default.SessionEffectData), null))
        };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, effects,
            effectCompleted: (scope, id) =>
            {
                var state = scope.QueryFirst("""
                    SELECT o.state,se.state FROM engine_outbox o JOIN engine_session se ON se.id=o.session_id WHERE o.id=$id
                    """, row => (Effect: row.Text(0), Session: row.Text(1)), new SqlArg("$id", id));
                scope.AfterCommit(() => committed.TrySetResult(state));
            });
        var state = await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("done", "idle"), state);
        Assert.Equal("idle", f.State("engine_session", "child"));
    }
}
