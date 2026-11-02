using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class CrossMachineStartTests
{
    [Fact]
    public async Task Target_owned_ancestor_uses_actual_full_title_and_provider_instead_of_stale_source_stub()
    {
        var title = new string('界', 1500000) + "\0😀ACTUAL_OWNER_SUFFIX";
        await using var f = await StartHosts.Open(foreignRootTitle: title); var h = f.H;
        h.Mac.Fixture.Db.Writer.Write("test.stale_display", scope => scope.Execute(
            "UPDATE engine_session SET title='stale foreign title',provider='stale-provider' WHERE id=$id", new SqlArg("$id", f.ForeignRoot)));
        var answer = await new McpMachineStarts(f.Starts).StartAsync(new(f.Parent, 0),
            new(Prompt: "Actual task", Title: "Child", Machine: h.Remote.Runtime.Machine), default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("starting", h.Remote.Fixture.State("engine_session", answer.SessionId));
        Assert.Equal(f.Parent, h.Remote.Fixture.Text("SELECT parent_id FROM engine_session WHERE id=$id", new SqlArg("$id", answer.SessionId)));
        Assert.Equal(title, h.Remote.Fixture.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id",
            row => Encoding.UTF8.GetString(row.Blob(0)), default, new SqlArg("$id", f.ForeignRoot)));
        Assert.Equal("stale foreign title", h.Mac.Fixture.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", f.ForeignRoot)));
        Assert.Equal("queued", h.Mac.Fixture.State("engine_message", answer.MessageId));
        Assert.Equal(1, h.Mac.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Actual_local_parent_full_title_above_frame_reaches_target_ancestry_and_initial_owning_M()
    {
        var title = new string('界', 1500000) + "\0😀FINAL_TITLE";
        await using var f = await StartHosts.Open(title: title); var h = f.H;
        var answer = await new McpMachineStarts(f.Starts).StartAsync(new(f.Parent, 0),
            new(Prompt: "TASK\0END", Title: "Child", Machine: h.Remote.Runtime.Machine), default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(title, h.Remote.Fixture.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id",
            row => Encoding.UTF8.GetString(row.Blob(0)), default, new SqlArg("$id", f.Parent)));
        var message = h.Mac.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB),CAST(sender_label AS BLOB) FROM engine_message WHERE id=$id",
            row => (Body: Encoding.UTF8.GetString(row.Blob(0)), Label: Encoding.UTF8.GetString(row.Blob(1))), default, new SqlArg("$id", answer.MessageId));
        Assert.StartsWith($"Session \"{title}\" ({f.Parent}) started you.", message.Body);
        Assert.EndsWith("TASK\0END", message.Body);
        Assert.Equal($"{title} ({f.Parent})", message.Label);
        Assert.Equal("queued", h.Mac.Fixture.State("engine_message", answer.MessageId));
        Assert.Equal(0, h.Remote.Fixture.Count("engine_message"));
    }
    [Theory]
    [InlineData("epoch=1")]
    [InlineData("reserved_epoch=1")]
    public async Task Actual_foreign_source_ownership_change_during_preparation_refuses_before_child(string mutation)
    {
        await using var f = await StartHosts.Open(foreignRootTitle: "Actual ancestor"); var h = f.H;
        using var pause = new PreparedPause(); h.Remote.WriteCommitted = pause.Committed;
        var starting = new McpMachineStarts(f.Starts).StartAsync(new(f.Parent, 0),
            new(Prompt: "Task", Title: "Child", Machine: h.Remote.Runtime.Machine), default);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Remote.Fixture.Db.Writer.Write("test.source_change", scope => scope.Execute(
                $"UPDATE engine_session SET {mutation} WHERE id=$id", new SqlArg("$id", f.ForeignRoot)));
        }
        finally { pause.Release.Set(); }
        var error = await Assert.ThrowsAsync<CallFailed>(() => starting);
        Assert.Equal("source_changed", error.Message);
        Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
    }

}
