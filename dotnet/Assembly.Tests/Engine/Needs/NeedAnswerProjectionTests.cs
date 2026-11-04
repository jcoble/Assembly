using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Reading;

namespace Assembly.Tests.Engine;

public sealed class NeedAnswerProjectionTests
{
    [Theory]
    [InlineData("{\"secret\":true}")]
    [InlineData("[\"private-secret\"]")]
    [InlineData("7")]
    [InlineData("true")]
    public void Unclassified_historical_answer_bodies_never_leave_entity_or_mirror_reads(string body)
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new OpenNeed("s", null, null, "question", "Recorded question", [], null, null, null),
            "n", actor: new(ActorKind.System, "adapter")).Applied);
        using var answer = JsonDocument.Parse(body);
        Assert.True(f.Apply(new ResolveNeed("answered", answer.RootElement), "n", 1).Applied);
        Assert.Equal(body, f.Text("SELECT answer_json FROM engine_needs_me WHERE id='n'"));
        var reader = new EngineReader(f.Db, f.Machine);
        Check(reader.Entity("needs_me", "n", default)!.Value);
        using var snapshot = reader.Snapshot("mirror", null, default);
        Check(Assert.Single(snapshot.ReadNext().Items, item => item.GetProperty("entityKind").GetString() == "needs_me"));
    }

    private static void Check(JsonElement item)
    {
        Assert.Equal("answered", item.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("answer").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("answerText").ValueKind);
        Assert.False(item.GetProperty("answerComplete").GetBoolean());
        Assert.DoesNotContain("secret", item.GetRawText());
    }
}
