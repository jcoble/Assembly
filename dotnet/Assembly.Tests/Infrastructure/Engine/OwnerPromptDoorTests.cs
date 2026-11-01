using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Engine;

public sealed class OwnerPromptDoorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    public async Task Prepared_owner_write_accepts_once_without_calling_the_provider(long? epoch)
    {
        await using var h = new Harness();
        var session = h.Sessions.Create("codex", "repo");
        h.Fixture.SeedSession(session.SessionId, state: "running", epoch: 7);
        var request = new OwnerPromptRequest(session.SessionId, "Exact text", "C", [], epoch);
        var prepared = await h.Door.PrepareOwnerPromptAsync(request, default);
        var accepted = h.Fixture.Db.Writer.Write("send/send", prepared);
        Assert.Equal($"prompt/{session.SessionId}/C", accepted.MessageId);
        Assert.Equal("queued", accepted.State); Assert.Null(accepted.RunId); Assert.Equal(7, accepted.Epoch);
        Assert.Equal(1, h.Fixture.Count("engine_message")); Assert.Equal(0, h.Fixture.Count("engine_outbox"));
        var retry = await h.Door.PrepareOwnerPromptAsync(request, default);
        Assert.Equal(accepted, h.Fixture.Db.Writer.Write("send/send", retry));
        Assert.Empty(h.Machine.Asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("é")]
    public async Task Invalid_client_is_refused_before_the_writer(string client)
    {
        await using var h = new Harness();
        var error = await Assert.ThrowsAsync<CallFailed>(() => h.Door.PrepareOwnerPromptAsync(new("s", "Text", client, [], null), default));
        Assert.Equal("clientId must be 1 to 64 letters, digits, - or _", error.Message);
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Other_session_attachment_and_negative_epoch_are_refused_without_rows()
    {
        await using var h = new Harness();
        var first = h.Sessions.Create("codex", "repo"); var second = h.Sessions.Create("codex", "repo");
        var other = h.Attachments.PathOf(second.SessionId, $"{Guid.NewGuid()}.png");
        await Assert.ThrowsAsync<CallFailed>(() => h.Door.PrepareOwnerPromptAsync(new(first.SessionId, "Text", "C", [other], null), default));
        await Assert.ThrowsAsync<CallFailed>(() => h.Door.PrepareOwnerPromptAsync(new(first.SessionId, "Text", "C", [], -1), default));
        Assert.Equal(0, h.Fixture.Count("engine_message")); Assert.Equal(0, h.Fixture.Count("engine_outbox"));
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Prepared_write_rechecks_ownership_in_the_supplied_transaction()
    {
        await using var h = new Harness();
        var session = h.Sessions.Create("codex", "repo");
        var prepared = await h.Door.PrepareOwnerPromptAsync(new(session.SessionId, "Text", "C", [], null), default);
        var error = Assert.Throws<CallFailed>(() => h.Fixture.Db.Writer.Write("test.moved", scope =>
        {
            scope.Execute("UPDATE session_info SET machine='other' WHERE session_id=$id", new SqlArg("$id", session.SessionId));
            return prepared(scope);
        }));
        Assert.Equal("session moved", error.Message);
        Assert.Null(h.Sessions.Get(session.SessionId)!.Machine); // Rejected write rolled its whole transaction back.
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public void Legacy_normalization_generates_once_and_keeps_supplied_order_and_original_epoch()
    {
        var old = JsonSerializer.Deserialize("{\"session\":\"s\",\"text\":\"Exact\\nAttached image: /old.png\",\"clientId\":null}", SessionsJson.Default.SendArgs)!;
        var first = SendFeature.OwnerRequest(old); var second = SendFeature.OwnerRequest(old);
        Assert.Equal(32, first.ClientId.Length); Assert.NotEqual(first.ClientId, second.ClientId);
        Assert.Equal(old.Text, first.Text); Assert.Empty(first.AttachmentIds); Assert.Null(first.Epoch);
        var supplied = SendFeature.OwnerRequest(new("s", "Exact", "C", ["two", "one"], 7));
        Assert.Equal("C", supplied.ClientId); Assert.Equal(new[] { "two", "one" }, supplied.AttachmentIds); Assert.Equal(7, supplied.Epoch);
        Assert.Throws<CallFailed>(() => SendFeature.OwnerRequest(new("s", "Text", "C", [], -1)));
    }

    [Fact]
    public void Attachment_ownership_checks_canonical_generated_paths_without_reading_files()
    {
        var store = new AttachmentStore(Path.Combine(Path.GetTempPath(), $"assembly-attachments-{Guid.NewGuid():N}"));
        var path = store.PathOf("s", $"{Guid.NewGuid()}.png");
        Assert.True(store.Owns("s", path)); Assert.False(store.Owns("other", path));
        Assert.False(store.Owns("s", path.Replace("/s/", "/s/../s/", StringComparison.Ordinal)));
        Assert.False(store.Owns("s", store.PathOf("s", "not-generated.png")));
        Assert.False(store.Owns("../s", path));
        Assert.NotNull(ImageType.ForExtension("png"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Topics _topics = new();
        public EngineFixture Fixture { get; }
        public SessionStore Sessions { get; }
        public AttachmentStore Attachments { get; } = new(Path.Combine(Path.GetTempPath(), $"assembly-attachments-{Guid.NewGuid():N}"));
        public EngineDoor Door { get; }
        public FakeMachine Machine { get; }
        private readonly EngineRuntime _runtime;
        public Harness()
        {
            Fixture = new(topics: _topics);
            Sessions = SessionStore.Open(Fixture.Db, Fixture.Time, "isolated-checkouts");
            Machine = new FakeMachine(null, Sessions);
            _runtime = EngineRuntime.Start(Fixture.Db, _topics, Fixture.Time, Fixture.Limits);
            Door = new(_runtime, Sessions, new MachineSet(Machine), Attachments, Fixture.Time);
        }
        public async ValueTask DisposeAsync() { await _runtime.DisposeAsync(); Fixture.Dispose(); }
    }
}
