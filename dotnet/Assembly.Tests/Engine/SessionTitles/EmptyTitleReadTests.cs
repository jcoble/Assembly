using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class EmptyTitleReadTests
{
    [Theory]
    [InlineData(false, "entity")]
    [InlineData(true, "entity")]
    [InlineData(false, "sessions")]
    [InlineData(true, "sessions")]
    [InlineData(false, "mirror")]
    [InlineData(true, "mirror")]
    public async Task Actual_null_chat_root_or_explicit_empty_title_projects_complete_empty_engine_title(bool explicitEmpty, string read)
    {
        await using var f = new Root(explicitEmpty);
        var item = read == "entity" ? f.Runtime.Reader.Entity("session", f.Id, default)!.Value : SnapshotItem(f, read);
        Assert.Equal("", item.GetProperty("title").GetString());
        Assert.False(item.TryGetProperty("titleComplete", out _));
        Assert.False(item.TryGetProperty("titleUtf8Bytes", out _));
        Assert.Equal(explicitEmpty ? "" : null, f.Sessions.Get(f.Id)!.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_root_empty_stat_and_zero_byte_eof_chunk_keep_identity_version_and_missing_row_refusal(bool explicitEmpty)
    {
        await using var f = new Root(explicitEmpty);
        var reader = f.Runtime.Reader;
        var stat = reader.SessionTitle(new(f.Id), default);
        Assert.Equal(f.Fixture.Machine, stat.EngineMachine); Assert.Equal(0, stat.Utf8Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("session-title/v1"u8.ToArray().Concat(new byte[8]).ToArray())), stat.Sha256);
        var chunk = reader.SessionTitleChunk(new(f.Id, stat.Version, stat.Epoch, 0, 0, Machine: stat.EngineMachine), default);
        Assert.Empty(chunk.Data); Assert.Null(chunk.NextOffset); Assert.Equal(0, chunk.TotalBytes); Assert.Equal(0, chunk.Offset);
        Assert.Equal(stat.Version, chunk.Version); Assert.Equal(stat.Epoch, chunk.Epoch);
        Assert.Throws<KeyNotFoundException>(() => reader.SessionTitle(new("absent"), default));
        Assert.Throws<KeyNotFoundException>(() => reader.SessionTitleChunk(new("absent", 0, 0, 0, 0), default));
        Assert.Throws<InvalidOperationException>(() => reader.SessionTitleChunk(new(f.Id, stat.Version + 1, stat.Epoch, 0, 0), default));
    }

    [Fact]
    public async Task Actual_reachable_directory_preserves_complete_empty_title_without_a_null_blob_read()
    {
        await using var f = new Root(false); f.Fixture.SeedSession("caller");
        using var scope = f.Fixture.Db.OpenSnapshot(default);
        Assert.Equal("", Assert.Single(SessionDirectory.Reachable(scope, "caller"), row => row.SessionId == f.Id).Title);
        var row = Assert.Single(SessionDirectory.ReachableDisplay(scope, "caller"), row => row.Entry.SessionId == f.Id);
        Assert.Equal("", row.Entry.Title); Assert.True(row.Complete); Assert.Equal(0, row.Bytes);
    }

    [Theory]
    [InlineData("", "Captured\0label")]
    [InlineData("", "")]
    [InlineData("Nonempty", "")]
    public async Task Actual_persisted_ordinary_framed_body_and_captured_label_remain_complete_when_empty(string body, string label)
    {
        await using var f = new Root(false);
        f.Fixture.Db.Writer.Write("test.persisted-terminal-message", scope => scope.Execute("""
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state,sender_label,framed)
            VALUES('m',1,$machine,1000,1000,'system',$to,CAST($body AS TEXT),'prompt','failed',CAST($label AS TEXT),1)
            """, new("$machine", f.Fixture.Machine), new("$to", f.Id), new("$body", Encoding.UTF8.GetBytes(body)), new("$label", Encoding.UTF8.GetBytes(label))));
        var item = f.Runtime.Reader.Entity("message", "m", default)!.Value;
        Assert.Equal(body, item.GetProperty("text").GetString()); Assert.Equal(1, item.GetProperty("textComplete").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), item.GetProperty("textUtf8Bytes").GetInt64());
        Assert.Equal(label, item.GetProperty("senderLabel").GetString()); Assert.False(item.TryGetProperty("senderLabelComplete", out _));
    }

    [Fact]
    public async Task Raw_owner_projection_retains_its_distinct_null_prefix_sentinel()
    {
        await using var f = new Root(false);
        var prepared = OwnerPromptFingerprint.Prepare(f.Fixture.Machine, new(f.Id, "raw owner body", "C", [], null), default);
        var id = OwnerPromptIdentity.MessageId(f.Id, "C");
        Assert.True(f.Fixture.Db.Writer.Write("owner", scope => f.Runtime.Commands.ApplyOwnerPrompt(scope,
            new(OwnerPromptIdentity.CommandId(f.Fixture.Machine, f.Id, "C"), f.Fixture.Machine, new(ActorKind.Window, "owner"), id, 0, 0, 1000), prepared)).Applied);
        var item = f.Runtime.Reader.Entity("message", id, default)!.Value;
        Assert.Equal(0, item.GetProperty("framed").GetInt32()); Assert.Equal("raw owner body", item.GetProperty("text").GetString());
        Assert.False(item.TryGetProperty("senderLabel", out _));
    }

    private static System.Text.Json.JsonElement SnapshotItem(Root f, string topic)
    {
        using var snapshot = f.Runtime.Reader.Snapshot(topic, null, default);
        return Assert.Single(snapshot.ReadNext().Items, item => item.GetProperty("id").GetString() == f.Id);
    }

    private sealed class Root : IAsyncDisposable
    {
        public EngineFixture Fixture { get; }
        public SessionStore Sessions { get; }
        public EngineRuntime Runtime { get; }
        public string Id { get; }
        public Root(bool explicitEmpty)
        {
            var topics = new Topics(); Fixture = new(topics: topics);
            Sessions = SessionStore.Open(Fixture.Db, Fixture.Time, "/checkouts");
            Id = Sessions.Create("claude", "repo").SessionId;
            if (explicitEmpty) Sessions.SetTitle(Id, "", "user");
            Runtime = EngineRuntime.Start(Fixture.Db, topics, Fixture.Time, Fixture.Limits,
                cutover: new(Fixture.Db, Sessions, new FakeLauncher(), "/checkouts", Fixture.Time));
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Fixture.Dispose(); }
    }
}
