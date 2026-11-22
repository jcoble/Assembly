using Assembly.Application.Common.Database;

namespace Assembly.Tests.Engine.Sessions;

public sealed class SessionHandleTests
{
    [Fact]
    public void Colliding_handles_use_only_as_much_of_the_session_id_as_needed()
    {
        using var fixture = new EngineFixture();
        // These IDs map to the same word pair and share their first eight characters.
        const string first = "test-session-560", second = "test-session-609", third = "test-session-1073";
        foreach (var id in new[] { first, second, third })
            fixture.SeedSession(id);

        string Handle(string id) => fixture.Db.QueryFirst("SELECT handle FROM session_handle WHERE session_id=$id",
            row => row.Text(0), default, new SqlArg("$id", id))!;
        var pair = Handle(first);
        Assert.Equal(pair + "-test-ses", Handle(second));
        Assert.Equal(pair + "-test-sess", Handle(third));
        Assert.Equal(3, new[] { pair, Handle(second), Handle(third) }.Distinct().Count());
    }
}
