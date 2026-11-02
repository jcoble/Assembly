using Assembly.Tests.Engine;
using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Delivery;

public sealed class AdapterWorkerTests
{
    [Fact]
    public async Task Slow_stop_on_one_session_does_not_delay_an_answer_on_another()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        f.SeedSession("slow", "running"); f.SeedSession("fast");
        Assert.True(f.Apply(new StopSession(), "slow").Applied);
        Assert.True(f.Apply(new OpenNeed("fast", null, null, "question", "Continue?", [], "real-request", null, null), "need", actor: new(ActorKind.System, "test")).Applied);
        Assert.True(f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), "need", 1).Applied);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeEffects
        {
            Execute = async (effect, ct) =>
        {
            if (effect.Kind == "stop")
            {
                stopping.TrySetResult(); await release.Task.WaitAsync(ct);
                return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SessionEffectData("idle", null, null)), null);
            }
            Assert.Equal("answer", effect.Kind); answered.TrySetResult();
            return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new AnswerEffectData("real-request")), null);
        }
        };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, fake);
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await answered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
    }
}
