using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Mcp;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Infrastructure.Phone;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Phone;

public sealed class PhoneChatRoutesTests
{
    private static readonly PhonePeer Paired = new("paired-device");
    private static Task<OwnerPromptAccepted> Immediate(string key, Func<Task<OwnerPromptAccepted>> run, CancellationToken ct) => run();

    [Fact]
    public async Task Mapped_compatibility_route_uses_only_the_admitted_peer_and_actual_engine_receipt()
    {
        await using var h = new Harness();
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var id = h.Session();
        PhonePeer? admitted = null;
        PhoneChatRoutes.Map(app, h.Door, h.Fixture.Db.Writer, _ => admitted, Immediate, h.EnsureCurrentPeer, h.IsLocalSession);
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>());
        Assert.Equal("/phone/v1/sessions/{id}/chat", endpoint.RoutePattern.RawText);
        var body = "{\"text\":\"actual route\",\"clientId\":\"C\",\"peer\":\"owner\"}";
        var anonymous = Context(id, body);
        anonymous.RequestServices = app.Services;
        await endpoint.RequestDelegate!(anonymous);
        Assert.Equal(401, anonymous.Response.StatusCode);
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        admitted = Paired;
        var paired = Context(id, body);
        paired.RequestServices = app.Services;
        await endpoint.RequestDelegate!(paired);
        Assert.Equal(200, paired.Response.StatusCode);
        paired.Response.Body.Position = 0;
        using var receipt = await JsonDocument.ParseAsync(paired.Response.Body);
        Assert.Equal("queued", receipt.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("itemId").ValueKind);
        Assert.Equal(1, h.Fixture.Count("engine_message"));
    }

    // The phone route takes a prompt and answers a receipt; it serves no rows. So this holds what it can: a prompt
    // that makes `named` and `attached` on its row goes through the route as it always did.
    [Fact]
    public async Task A_prompt_naming_files_and_images_is_sent_exactly_and_answers_only_the_receipt()
    {
        await using var h = new Harness();
        var id = h.Session();
        const string text = "see /a/b.md\n\nAttached image: /s/a.png (a.png)";
        var sent = await h.Send(id, JsonSerializer.Serialize(new { text, clientId = "named_1" }));
        Assert.Equal(200, sent.Status);
        Assert.Equal(
            ["itemId", "sessionId", "messageId", "clientId", "state", "messageVersion", "seq", "epoch", "runId"],
            sent.Body.EnumerateObject().Select(property => property.Name));
        var manifest = Assert.Single((await h.Door.ReadOwnerPromptsAsync(new(id), default)).Items);
        var content = await h.Door.ReadOwnerPromptChunkAsync(new(id, "named_1", manifest.ContentSha256, "text", 0), default);
        Assert.Equal(text, Encoding.UTF8.GetString(content.Data));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    public async Task Real_door_queues_exact_payload_replays_original_receipt_after_reopen_and_never_invents_an_item(long? epoch)
    {
        await using var h = new Harness();
        var id = h.Session();
        var attachments = new[] { h.Image(id), h.Image(id) };
        const string text = "  exact\n😀\0end  ";
        var body = JsonSerializer.Serialize(new { text, clientId = "stable_1", attachmentIds = attachments, epoch });
        string? key = null;
        var first = await h.Send(id, body, order: (value, run, ct) => { key = value; return run(); });
        Assert.Equal(200, first.Status);
        Assert.Equal($"session {id}", key);
        Assert.Equal(id, first.Body.GetProperty("sessionId").GetString());
        Assert.Equal($"prompt/{id}/stable_1", first.Body.GetProperty("messageId").GetString());
        Assert.Equal("stable_1", first.Body.GetProperty("clientId").GetString());
        Assert.Equal("queued", first.Body.GetProperty("state").GetString());
        Assert.Equal(7, first.Body.GetProperty("epoch").GetInt64());
        Assert.Equal(JsonValueKind.Null, first.Body.GetProperty("itemId").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.Body.GetProperty("runId").ValueKind);
        var page = await h.Door.ReadOwnerPromptsAsync(new(id), default);
        var manifest = Assert.Single(page.Items);
        Assert.Equal(epoch, manifest.TargetEpoch);
        var content = await h.Door.ReadOwnerPromptChunkAsync(new(id, "stable_1", manifest.ContentSha256, "text", 0), default);
        Assert.Equal(text, Encoding.UTF8.GetString(content.Data));
        var references = await h.Door.ReadOwnerPromptChunkAsync(new(id, "stable_1", manifest.ContentSha256, "attachments", 0), default);
        Assert.Equal(attachments, JsonSerializer.Deserialize<string[]>(references.Data));
        await h.Reopen();
        var replay = await h.Send(id, body);
        Assert.Equal(200, replay.Status);
        Assert.Equal(first.Body.GetRawText(), replay.Body.GetRawText());
        Assert.Equal(1, h.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Fixture.Count("engine_outbox"));
        Assert.Empty(h.Machine.Asked);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("attachment-order")]
    [InlineData("epoch")]
    public async Task Reusing_client_with_changed_payload_conflicts_without_another_message(string changed)
    {
        await using var h = new Harness();
        var id = h.Session();
        var attachments = new[] { h.Image(id), h.Image(id) };
        Assert.Equal(200, (await h.Send(id, JsonSerializer.Serialize(new { text = "original", clientId = "C", attachmentIds = attachments, epoch = 7 }))).Status);
        var conflict = await h.Send(id, JsonSerializer.Serialize(new
        {
            text = changed == "text" ? "changed" : "original",
            clientId = "C",
            attachmentIds = changed == "attachment-order" ? attachments.Reverse().ToArray() : attachments,
            epoch = changed == "epoch" ? 8 : 7
        }));
        Assert.Equal(409, conflict.Status);
        Assert.Equal("client_id_conflict", conflict.Body.GetProperty("error").GetString());
        Assert.Equal(1, h.Fixture.Count("engine_message"));
        Assert.Empty(h.Machine.Asked);
    }

    [Fact]
    public async Task Missing_peer_is_refused_before_body_read_or_ordering_even_if_body_claims_owner()
    {
        await using var h = new Harness();
        var context = Context("absent", "");
        context.Request.Body = new UnreadableBody();
        await PhoneChatRoutes.HandleAsync(context, null, h.Door, h.Fixture.Db.Writer,
            (_, _, _) => throw new InvalidOperationException("Unauthenticated request reached the queue"), h.EnsureCurrentPeer, h.IsLocalSession);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
        Assert.Empty(h.Machine.Asked);
    }

    [Theory]
    [InlineData("{\"text\":\"hi\"}")]
    [InlineData("{\"text\":\"hi\",\"clientId\":\"bad client\"}")]
    [InlineData("{\"text\":\"hi\",\"clientId\":\"C\",\"epoch\":-1}")]
    [InlineData("{\"text\":\"hi\",\"clientId\":\"C\",\"epoch\":1.5}")]
    [InlineData("{\"text\":\"hi\",\"clientId\":\"C\",\"attachmentIds\":null}")]
    [InlineData("{\"text\":\"hi\",\"clientId\":\"C\",\"attachmentIds\":[1]}")]
    [InlineData("{\"text\":\" \",\"clientId\":\"C\"}")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task Invalid_bodies_are_refused_before_ordering_or_writing(string body)
    {
        await using var h = new Harness();
        var response = await h.Send(h.Session(), body, order: (_, _, _) => throw new InvalidOperationException("Invalid request reached the queue"));
        Assert.Equal(400, response.Status);
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Existing_text_image_and_body_limits_apply_to_known_and_chunked_lengths()
    {
        await using var h = new Harness();
        var id = h.Session();
        Assert.Equal(400, (await h.Send(id, JsonSerializer.Serialize(new { text = new string('x', 10001), clientId = "C" }))).Status);
        Assert.Equal(400, (await h.Send(id, JsonSerializer.Serialize(new { text = "x", clientId = "C", attachmentIds = Enumerable.Repeat("image", 9).ToArray() }))).Status);
        var huge = JsonSerializer.Serialize(new { text = new string('x', PhoneChatRoutes.MaxBodyBytes), clientId = "C" });
        foreach (var knownLength in new[] { true, false })
        {
            var context = Context(id, huge);
            if (!knownLength) context.Request.ContentLength = null;
            await PhoneChatRoutes.HandleAsync(context, Paired, h.Door, h.Fixture.Db.Writer, Immediate, h.EnsureCurrentPeer, h.IsLocalSession);
            Assert.Equal(413, context.Response.StatusCode);
        }
        Assert.Equal(0, h.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Real_door_refuses_unknown_native_child_stale_epoch_and_other_sessions_image()
    {
        await using var h = new Harness();
        var id = h.Session();
        var other = h.Session();
        Assert.Equal(404, (await h.Send("unknown", "{\"text\":\"x\",\"clientId\":\"C\"}")).Status);
        Assert.Equal(400, (await h.Send(id, JsonSerializer.Serialize(new { text = "x", clientId = "C", attachmentIds = new[] { h.Image(other) } }))).Status);
        Assert.Equal(400, (await h.Send(id, "{\"text\":\"x\",\"clientId\":\"C\",\"epoch\":6}")).Status);
        h.Fixture.Db.Writer.Write("test.native-child", scope => scope.Execute(
            "UPDATE session_info SET parent_session_id=$parent,helper=0 WHERE session_id=$id", new("$parent", other), new SqlArg("$id", id)));
        var native = await h.Send(id, "{\"text\":\"x\",\"clientId\":\"C\"}");
        Assert.Equal(400, native.Status);
        Assert.Equal("a subagent's session takes prompts from its parent only", native.Body.GetProperty("error").GetString());
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.Empty(h.Machine.Asked);
    }

    [Fact]
    public async Task Preparation_and_writer_wait_for_the_supplied_session_queue()
    {
        await using var h = new Harness();
        var id = h.Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = h.Send(id, "{\"text\":\"x\",\"clientId\":\"C\"}", order: async (key, run, ct) =>
        {
            Assert.Equal($"session {id}", key);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return await run();
        });
        await entered.Task;
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        release.SetResult();
        Assert.Equal(200, (await pending).Status);
        Assert.Equal(1, h.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Cancelled_admission_wait_cannot_prepare_or_commit_a_prompt()
    {
        await using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        var context = Context(h.Session(), "{\"text\":\"x\",\"clientId\":\"C\"}");
        context.RequestAborted = cancellation.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PhoneChatRoutes.HandleAsync(context, Paired, h.Door,
            h.Fixture.Db.Writer, (_, run, _) =>
            {
                cancellation.Cancel();
                return run();
            }, h.EnsureCurrentPeer, h.IsLocalSession));
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Device_revoked_while_queued_is_refused_in_the_writer_before_engine_apply()
    {
        await using var h = new Harness();
        var response = await h.Send(h.Session(), "{\"text\":\"x\",\"clientId\":\"C\"}", order: (_, run, _) =>
        {
            h.Fixture.Db.Writer.Write("test.revoke", scope => scope.Execute(
                "UPDATE phone_devices SET revoked_at=1 WHERE id=$id", new SqlArg("$id", Paired.DeviceId)));
            return run();
        });
        Assert.Equal(401, response.Status);
        Assert.Equal(1, h.CurrentPeerChecks);
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
        Assert.Empty(h.Machine.Asked);
    }

    [Fact]
    public async Task Foreign_session_is_refused_before_door_preparation_or_remote_IO()
    {
        await using var h = new Harness();
        var id = h.Session();
        h.Fixture.Db.Writer.Write("test.foreign", scope => scope.Execute(
            "UPDATE session_info SET machine='workbox-test' WHERE session_id=$id", new SqlArg("$id", id)));
        var response = await h.Send(id, "{\"text\":\"x\",\"clientId\":\"C\"}");
        Assert.Equal(404, response.Status);
        Assert.Equal("unknown session", response.Body.GetProperty("error").GetString());
        // EngineDoor would fail with "unknown owning machine" if it reached preparation for this foreign row.
        Assert.Equal(0, h.CurrentPeerChecks);
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
        Assert.Empty(h.Machine.Asked);
    }

    [Fact]
    public async Task Actual_door_refuses_foreign_session_even_when_host_local_check_was_stale()
    {
        await using var h = new Harness();
        var id = h.Session();
        h.Fixture.Db.Writer.Write("test.moved-after-host-read", scope => scope.Execute(
            "UPDATE session_info SET machine='workbox-test' WHERE session_id=$id", new SqlArg("$id", id)));
        var hostChecks = 0;
        var response = await h.Send(id, "{\"text\":\"x\",\"clientId\":\"C\",\"localOnly\":false}", isLocalSession: _ =>
        {
            hostChecks++;
            return true; // The host's earlier local observation lost to an ownership move.
        });
        Assert.Equal(1, hostChecks);
        Assert.Equal(400, response.Status);
        Assert.Equal("session moved", response.Body.GetProperty("error").GetString());
        Assert.Equal(0, h.CurrentPeerChecks);
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
        Assert.Equal(0, h.Fixture.Count("engine_outbox"));
        Assert.Empty(h.Machine.Asked);
    }

    private static DefaultHttpContext Context(string id, string body)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.RouteValues["id"] = id;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class UnreadableBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unauthenticated request read its body");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Topics _topics = new();
        public EngineFixture Fixture { get; }
        public SessionStore Sessions { get; private set; }
        public FakeMachine Machine { get; private set; }
        public EngineDoor Door { get; private set; } = null!;
        private readonly AttachmentStore _attachments = new(Path.Combine(Path.GetTempPath(), $"assembly-phone-chat-{Guid.NewGuid():N}"));
        private EngineRuntime _runtime = null!;
        private LiveHub _hub = null!;
        private BackgroundWork _work = null!;
        private SessionRegistry _hosts = null!;
        private NeedAnswers _needs = null!;
        public int CurrentPeerChecks { get; private set; }

        public Harness()
        {
            Fixture = new(topics: _topics);
            Fixture.Db.Writer.Write("test.phone-device", scope =>
            {
                scope.Execute("""
                    CREATE TABLE phone_devices (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE,
                        fcm_token TEXT, created_at INTEGER NOT NULL, last_seen_at INTEGER, revoked_at INTEGER
                    )
                    """);
                return scope.Execute("INSERT INTO phone_devices(id,name,token_hash,created_at) VALUES($id,'Test phone','hash',0)", new SqlArg("$id", Paired.DeviceId));
            });
            Sessions = SessionStore.Open(Fixture.Db, Fixture.Time, "isolated-checkouts");
            Machine = new FakeMachine(null, Sessions);
            Open();
        }
        public string Session()
        {
            var id = Sessions.Create("codex", "repo").SessionId;
            Fixture.SeedSession(id, state: "running", epoch: 7);
            return id;
        }
        public string Image(string id) => _attachments.PathOf(id, $"{Guid.NewGuid()}.png");
        public bool IsLocalSession(string id) => Sessions.Get(id) is { Machine: null };
        public void EnsureCurrentPeer(IWriteScope scope, PhonePeer peer)
        {
            CurrentPeerChecks++;
            if (scope.QueryFirst("SELECT id FROM phone_devices WHERE id=$id AND revoked_at IS NULL", row => row.Text(0),
                new SqlArg("$id", peer.DeviceId)) is null) throw new UnauthorizedAccessException();
        }
        public async Task<(int Status, JsonElement Body)> Send(string id, string body, PhoneChatOrder? order = null,
            Func<string, bool>? isLocalSession = null)
        {
            var context = Context(id, body);
            await PhoneChatRoutes.HandleAsync(context, Paired, Door, Fixture.Db.Writer, order ?? Immediate, EnsureCurrentPeer, isLocalSession ?? IsLocalSession);
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            return (context.Response.StatusCode, document.RootElement.Clone());
        }
        public async Task Reopen()
        {
            await Close();
            Fixture.Reopen();
            Sessions = SessionStore.Open(Fixture.Db, Fixture.Time, "isolated-checkouts");
            Machine = new FakeMachine(null, Sessions);
            Open();
        }
        private void Open()
        {
            _hub = new LiveHub(RowStore.Open(Fixture.Db));
            _work = new BackgroundWork(Fixture.Time, TextWriter.Null);
            _hosts = new SessionRegistry(_hub, Sessions, new FakeLauncher(), Fixture.Time, TextWriter.Null, _work);
            _runtime = EngineRuntime.Start(Fixture.Db, _topics, Fixture.Time, Fixture.Limits);
            _needs = new NeedAnswers(new McpGate(_runtime, Fixture.Limits, Fixture.Time), _runtime, _hosts, Fixture.Time);
            Door = new(_runtime, Sessions, new MachineSet(Machine), _attachments, Fixture.Time, _needs);
        }
        private async Task Close()
        {
            await _needs.DisposeAsync();
            await _runtime.DisposeAsync();
            await _hosts.DisposeAsync();
            await _work.DisposeAsync();
            _hub.Dispose();
        }
        public async ValueTask DisposeAsync() { await Close(); Fixture.Dispose(); }
    }
}
