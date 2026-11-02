using System.Text.Json;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class TerminalReplyTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly AppDatabase _db;
    private readonly RowStore _store;
    public TerminalReplyTests() { _db = TestDatabase.Open(_file.Path); _store = RowStore.Open(_db); }
    private void Add(string run, string kind, string payload, long at = 100) => _store.Commit("child", new RowBatch
    {
        Rows = { new(run, null, kind, JsonDocument.Parse(payload).RootElement.Clone(), at) }
    });
    private FinalizedChatReply? Read(string run) => _db.Writer.Write("test.read", s => RowStore.TerminalReply(s, "child", run));

    [Theory]
    [InlineData("finished")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Exact_run_terminal_boundary_preserves_last_legacy_plain_text_and_error(string state)
    {
        Add("run", "turn", "{\"state\":\"started\"}");
        Add("run", "message", "{\"blocks\":[{\"kind\":\"paragraph\",\"parts\":[{\"value\":\"first\"}]}]}");
        Add("other", "message", "{\"blocks\":[{\"value\":\"other run\"}]}");
        const string blocks = "[{\"kind\":\"paragraph\",\"parts\":[{\"value\":\"hello \"},{\"value\":\"世界\\u0000\"}]},{\"kind\":\"list\",\"items\":[[{\"value\":\"one\"}],[{\"value\":\"two\"}]]},{\"kind\":\"code\",\"value\":\"x\\ny\"}]";
        Add("run", "message", "{\"blocks\":" + blocks + "}");
        Add("run", "error", "{\"message\":\"original error\",\"code\":\"original\"}");
        Add("run", "turn", "{\"state\":\"" + state + "\"}", 222);
        using var hub = new LiveHub(_store);
        var old = RunAnswer.Of(hub, "child", "run")!;
        Add("run", "message", "{\"blocks\":[{\"value\":\"too late\"}]}");
        Add("run", "error", "{\"message\":\"too late\"}");
        var reply = Assert.IsType<FinalizedChatReply>(Read("run"));
        Assert.Equal(old.Reply, reply.Text); Assert.Equal("hello 世界\0\n\none\ntwo\n\nx\ny", reply.Text);
        Assert.Equal(old.Error, reply.Error); Assert.Equal(6, reply.TerminalSeq); Assert.Equal(4, reply.MessageSeq); Assert.Equal(5, reply.ErrorSeq);
        Assert.Equal(("child", "run", state, 222L), (reply.SessionId, reply.ChatRunId, reply.TerminalState, reply.EndedAt));
        Assert.Equal(reply, _db.Writer.Write("test.exact", s => RowStore.TerminalReplyAt(s, "child", "run", 6)));
        Assert.Null(_db.Writer.Write("test.wrong", s => RowStore.TerminalReplyAt(s, "child", "other", 6)));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("[]", "")]
    [InlineData("[{\"value\":\"  \\n\\t\"}]", "  \n\t")]
    public void Missing_or_empty_message_is_a_real_exact_empty_projection(string? blocks, string expected)
    {
        Add("r", "turn", "{\"state\":\"started\"}"); Assert.Null(Read("r"));
        if (blocks is not null) Add("r", "message", "{\"blocks\":" + blocks + "}");
        Add("r", "turn", "{\"state\":\"failed\"}");
        Assert.Equal(expected, Read("r")!.Text);
        Assert.Equal(blocks is null, Read("r")!.MessageSeq is null);
    }

    [Fact]
    public void Malformed_terminal_or_message_is_never_guessed()
    {
        Add("r", "turn", "{\"state\":3}"); Assert.ThrowsAny<Exception>(() => Read("r"));
        Add("r", "message", "{\"blocks\":3}"); Add("r", "turn", "{\"state\":\"finished\"}");
        Assert.ThrowsAny<Exception>(() => Read("r"));
    }
    public void Dispose() { _db.Dispose(); _file.Dispose(); }
}
