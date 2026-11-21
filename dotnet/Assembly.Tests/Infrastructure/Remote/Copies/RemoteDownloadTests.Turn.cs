using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Infrastructure.Remote.Copies;

/// <summary>A copied run's user rows and tool details are read again at turn end (TSK-1692 steps 9 and 12).</summary>
public sealed partial class RemoteDownloadTests
{
    // Pins: spec test 20.
    [Fact]
    public async Task Spec20_a_user_row_acknowledged_after_copy_shows_acknowledged()
    {
        Seed("s", 10);
        Assert.True(_remoteRows.Copy(new StoredRow("s", 11, "run-1", null, RowKind.User,
            JsonSerializer.SerializeToElement(new UserPayload("hello", ClientId: "c1"), RowJsonContext.Rows.UserPayload), 11, PrevSeq: 10)));
        Assert.True(_remoteRows.Copy(new StoredRow("s", 12, "run-1", "turn-1", RowKind.Turn,
            JsonSerializer.SerializeToElement(new { state = "started" }), 12, PrevSeq: 11)));
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        Assert.Null(MacUser("s", 11).Acknowledgment);
        using var viewer = _hosts.MacHub.Subscribe("s", 12);
        Machine.WatchRun("s", "run-1");
        var reread = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _onAnswered = (method, session) => { if (method == "turn" && session == "s") reread.TrySetResult(); };

        // Acknowledged in place on the remote after the copy saved it, then the turn ends.
        var acknowledged = new UserAcknowledgment("steer_injected", null);
        _hosts.Remote.Fixture.Db.Writer.Write("test.ack", scope => { _remoteRows.AcknowledgeUser(scope, "s", "c1", acknowledged); return 0; });
        Assert.True(_remoteHub!.PublishCopy(new StoredRow("s", 13, "run-1", "turn-1", RowKind.Turn,
            JsonSerializer.SerializeToElement(new { state = "finished" }), 13, PrevSeq: 12)));
        Assert.Equal(13, (await viewer.Queue.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Seq);
        await reread.Task.WaitAsync(Wait);
        // The loop takes one piece of work at a time: once a later download's first chunk is saved, the re-read is too.
        Seed("after-s", 1);
        await Machine.EnsureCopyAsync("after-s", default).WaitAsync(Wait);

        Assert.Equal(acknowledged, MacUser("s", 11).Acknowledgment);
        var page = await _hosts.MacHub.PageAsync("s", long.MaxValue, 50, default);
        var user = page.Rows.Single(row => row.Seq == 11);
        Assert.Equal(acknowledged, user.Payload.Deserialize(RowJsonContext.Rows.UserPayload)!.Acknowledgment);
    }

    [Fact]
    public async Task A_finished_turn_s_tool_details_are_saved_at_turn_end()
    {
        Seed("s", 10);
        Assert.True(_remoteRows.Copy(new StoredRow("s", 11, "run-1", "turn-1", RowKind.Turn,
            JsonSerializer.SerializeToElement(new { state = "started" }), 11, PrevSeq: 10)));
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        using var viewer = _hosts.MacHub.Subscribe("s", 11);
        Machine.WatchRun("s", "run-1");
        var reread = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _onAnswered = (method, session) => { if (method == "turn" && session == "s") reread.TrySetResult(); };

        // The tool runs live: its row is copied as it comes, its detail never is. Then the turn ends.
        _remoteRows.SaveToolDetail("s", new ToolDetail("tool-1", Input: "{\"command\":\"ls\"}", Output: "a\nb\n"));
        Assert.True(_remoteHub!.PublishCopy(new StoredRow("s", 12, "run-1", "tool-1", RowKind.Tool,
            JsonSerializer.SerializeToElement(new { summary = "ls" }), 12, PrevSeq: 11)));
        Assert.Equal(12, (await viewer.Queue.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Seq);
        Assert.Null(_hosts.MacHub.ToolCall("s", "tool-1"));
        Assert.True(_remoteHub.PublishCopy(new StoredRow("s", 13, "run-1", "turn-1", RowKind.Turn,
            JsonSerializer.SerializeToElement(new { state = "finished" }), 13, PrevSeq: 12)));
        Assert.Equal(13, (await viewer.Queue.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Seq);
        await reread.Task.WaitAsync(Wait);
        // The loop takes one piece of work at a time: once a later download's first chunk is saved, the re-read is too.
        Seed("after-s", 1);
        await Machine.EnsureCopyAsync("after-s", default).WaitAsync(Wait);

        var saved = _hosts.MacHub.ToolCall("s", "tool-1");
        Assert.Equal(("{\"command\":\"ls\"}", "a\nb\n", 4L), (saved?.Input, saved?.Output, saved?.OutputBytes));
    }

    private UserPayload MacUser(string session, long seq) => JsonSerializer.Deserialize(_hosts.Mac.Fixture.Db.QueryFirst(
        "SELECT payload FROM rows WHERE session_id=$s AND seq=$q", row => row.Text(0), default,
        new Assembly.Application.Common.Database.SqlArg("$s", session), new Assembly.Application.Common.Database.SqlArg("$q", seq))!,
        RowJsonContext.Rows.UserPayload)!;
}
