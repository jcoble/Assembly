namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Theory]
    [InlineData("sessions", "session")]
    [InlineData("runs", "run")]
    [InlineData("needs_me", "needs_me")]
    [InlineData("messages", "message")]
    [InlineData("prs", "pr")]
    [InlineData("usage", "usage")]
    public async Task Each_topic_replays_since_and_snapshots_on_gap(string topic, string kind)
    {
        Event(kind, "one"); Event(kind, "other");
        await Published();
        using var socket = await Open();
        await Send(socket, $$"""{"t":"sub","id":7,"stream":"engine","topic":"{{topic}}","key":"one","after":0}""");
        var page = await Read(socket);
        Assert.Equal("engine_events", page.GetProperty("t").GetString());
        Assert.Equal(2, page.GetProperty("through").GetInt64());
        Assert.Equal("one", Assert.Single(page.GetProperty("events").EnumerateArray()).GetProperty("entityId").GetString());
        await Send(socket, """{"t":"unsub","id":7}""");
        _f.Db.Writer.Write("gap", s => s.Execute("DELETE FROM engine_event WHERE seq=1"));
        await Send(socket, $$"""{"t":"sub","id":8,"stream":"engine","topic":"{{topic}}","after":0}""");
        Assert.Equal("engine_snapshot", (await Read(socket)).GetProperty("t").GetString());
    }
}
