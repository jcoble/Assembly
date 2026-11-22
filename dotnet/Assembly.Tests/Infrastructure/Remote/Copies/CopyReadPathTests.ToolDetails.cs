using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Infrastructure.Remote.Copies;

/// <summary>A saved session's tool details download with its rows and open from the copy (TSK-1692 step 12).</summary>
public sealed partial class CopyReadPathTests
{
    // Pins: the spec's tool test.
    [Fact]
    public async Task A_terminal_tool_and_a_tool_with_over_1MB_output_show_their_saved_part_and_true_size_offline()
    {
        Seed("s", 20);
        Terminal("s", "terminal-tool", "hello");
        Tool("s", "big", new ToolDetail("big", Input: "{}", Output: new string('x', 1_500_000)));
        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        await _hosts.DisconnectMirror();
        _calls.Clear();

        var terminal = Ok(await ToolRead("terminal-tool"));
        var big = Ok(await ToolRead("big"));

        Assert.Equal("hello", terminal.GetProperty("output").GetString());
        Assert.Equal(5, terminal.GetProperty("outputBytes").GetInt64());
        Assert.False(terminal.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, terminal.GetProperty("terminals")[0].GetProperty("exitStatus").GetProperty("exitCode").GetInt64());
        Assert.Equal(RowsFeature.ToolCap, Encoding.UTF8.GetByteCount(big.GetProperty("output").GetString()!));
        Assert.Equal(1_500_000, big.GetProperty("outputBytes").GetInt64());
        Assert.True(big.GetProperty("truncated").GetBoolean());
        Assert.Empty(_calls);
    }

    // Pins: review focus 5. Each control character's JSON is six bytes, so the full 1 MB cut alone would pass 4 MB.
    [Fact]
    public async Task An_item_with_a_huge_tool_output_downloads_in_one_frame_with_its_true_size()
    {
        Seed("s", 20);
        Tool("s", "huge", new ToolDetail("huge", Output: new string('\u0001', 3_000_000)));

        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        await _hosts.DisconnectMirror();
        var huge = Ok(await ToolRead("huge"));

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Equal(3_000_000, huge.GetProperty("outputBytes").GetInt64());
        Assert.True(huge.GetProperty("truncated").GetBoolean());
        Assert.NotEmpty(huge.GetProperty("output").GetString()!);
    }

    [Fact]
    public async Task A_running_tool_is_read_through_and_not_saved()
    {
        Seed("s", 20);
        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        // A tool of a turn still running on the remote, saved there after the copy.
        Tool("s", "running", new ToolDetail("running", Output: "partial"), run: "run-2");
        _calls.Clear();

        var connected = Ok(await ToolRead("running"));
        await _hosts.DisconnectMirror();
        var offline = await ToolRead("running");

        Assert.Equal("partial", connected.GetProperty("output").GetString());
        Assert.Contains("rows/toolRaw", _calls);
        Assert.Equal(0, _hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM tool_details WHERE item_id='running'"));
        Assert.Equal("error", offline.GetProperty("t").GetString());
        Assert.Equal("unknown tool call", offline.GetProperty("message").GetString());
    }

    private Task<JsonElement> ToolRead(string item) => Call("rows", "tool", new JsonObject { ["session"] = "s", ["item"] = item });

    // A tool row with its detail, saved on the remote next in the session.
    private void Tool(string session, string item, ToolDetail detail, string run = "run")
    {
        var seq = NextRemoteSeq(session);
        Assert.True(_remoteRows.Copy(new StoredRow(session, seq, run, item, RowKind.Tool,
            JsonSerializer.SerializeToElement(new { summary = item }), seq, PrevSeq: seq - 1)));
        _remoteRows.SaveToolDetail(session, detail);
    }

    // A terminal tool that ran to exit 0 printing output, as the provider reports it, saved on the remote.
    private void Terminal(string session, string item, string output)
    {
        var reader = new UpdateReader();
        var converter = new RowConverter();
        foreach (var update in new[]
        {
            $$"""{"sessionUpdate":"tool_call_update","toolCallId":"{{item}}","status":"in_progress","content":[{"type":"terminal","terminalId":"T"}]}""",
            $$"""{"sessionUpdate":"terminal_output_chunk","terminalId":"T","data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(output))}}"}""",
            """{"sessionUpdate":"terminal_update","terminalId":"T","exitStatus":{"exitCode":0}}""",
            $$"""{"sessionUpdate":"tool_call_update","toolCallId":"{{item}}","status":"completed","content":[]}""",
        })
            foreach (var change in reader.Read(new SessionNotification("provider", JsonDocument.Parse(update).RootElement.Clone()), 1))
                _remoteRows.Commit(session, converter.Apply(change));
    }

    private long NextRemoteSeq(string session) => _hosts.Remote.Fixture.Db.QueryFirst(
        "SELECT next_seq FROM sessions WHERE session_id=$s", row => row.Long(0), default, new SqlArg("$s", session));
}
