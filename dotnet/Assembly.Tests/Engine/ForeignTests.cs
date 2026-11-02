using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine;

public sealed class ForeignTests
{
    private static EngineActor Peer(string machine = "source") => new(ActorKind.System, "machine:" + machine);

    [Fact]
    public async Task Stub_is_nonoperational_invisible_and_idempotent_across_restart()
    {
        using var f = new EngineFixture();
        var id = Guid.NewGuid();
        var command = new RegisterForeignSession("source", "custom-provider", "Source title");
        var receipt = f.Apply(command, "parent", actor: Peer(), commandId: id);
        Assert.True(receipt.Applied, receipt.Error);
        Assert.Equal(receipt, f.Apply(command, "parent", actor: Peer(), commandId: id));
        Assert.True(f.Apply(command with { Title = "Ignored duplicate" }, "parent", actor: Peer()).Applied);
        Assert.Equal(1, f.Count("engine_session"));
        Assert.Equal("Source title", f.Text("SELECT title FROM engine_session"));
        Assert.Equal("source", f.Text("SELECT holder FROM engine_session"));
        Assert.Null(f.Text("SELECT model FROM engine_session"));
        Assert.Null(f.Text("SELECT provider_session_id FROM engine_session"));
        Assert.Equal("", f.Text("SELECT folder FROM engine_session"));
        Assert.Equal("", f.Text("SELECT account_context FROM engine_session"));
        Assert.Equal(0, f.Number("SELECT version+epoch+managed_checkout+can_steer FROM engine_session"));
        Assert.Empty(f.Events()); Assert.Equal(0, f.Count("engine_outbox"));
        f.Reopen();
        await using var runtime = EngineRuntime.Start(f.Db, new(), f.Time, f.Limits);
        Assert.Null(runtime.Reader.Entity("session", "parent", default));
        using var sessionPage = runtime.Reader.Snapshot("sessions", null, default);
        using var mirrorPage = runtime.Reader.Snapshot("mirror", null, default);
        Assert.Empty(sessionPage.ReadNext().Items); Assert.Empty(mirrorPage.ReadNext().Items);
        Assert.Null(((Assembly.Engine.Hosting.IEngineRuntime)runtime).Read(scope => new McpGate(runtime, f.Limits, f.Time).Resolve(scope, new("parent", 0)), default));
    }

    [Fact]
    public void Registration_refuses_local_identity_shadow_and_source_forgery_with_immutable_receipts()
    {
        using var f = new EngineFixture(); f.SeedSession("local");
        foreach (var (id, source, actor) in new[] { ("local", "source", Peer()), ("foreign", f.Machine, Peer(f.Machine)), ("foreign", "source", Peer("other")), ("foreign", "source", new EngineActor(ActorKind.Window, "owner")) })
            Assert.Equal("forbidden", f.Apply(new RegisterForeignSession(source, "p", "T"), id, actor: actor).Error);
        Assert.Equal("bad_args", f.Apply(new RegisterForeignSession("source", "p", "T"), "foreign", version: 1, actor: Peer()).Error);
        Assert.Equal(1, f.Count("engine_session")); Assert.Empty(f.Events()); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.True(f.Apply(new RegisterForeignSession("source", "p", "T"), "foreign", actor: Peer()).Applied);
        Assert.Equal("forbidden", f.Apply(new RegisterForeignSession("other", "p", "T"), "foreign", actor: Peer("other")).Error);
        Assert.Equal("invalid_transition", f.Apply(new StopSession(), "foreign").Error);
        Assert.Equal("invalid_transition", f.Apply(new SetSessionState("idle", null, null), "foreign", actor: Peer()).Error);
    }

    [Fact]
    public void Ordinary_peer_child_requires_matching_nonlocal_parent_and_skips_source_limits()
    {
        using var f = new EngineFixture(new(() => 0, () => 0));
        Assert.True(f.Apply(new RegisterForeignSession("source", "p", "T"), "parent", actor: Peer()).Applied);
        var start = new StartSession("configured-target", null, "/target", "Child", "parent", null, null);
        Assert.Equal("forbidden", f.Apply(start, "window").Error);
        Assert.Equal("forbidden", f.Apply(start, "wrong-source", actor: Peer("other")).Error);
        Assert.True(f.Apply(start, "child", actor: Peer()).Applied);
        Assert.Equal("starting", f.State("engine_session", "child"));
        Assert.Null(f.Text("SELECT model FROM engine_session WHERE id='child'"));
        Assert.Equal(1, f.Count("engine_outbox")); Assert.Single(f.Events());
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("interrupt")]
    public void Foreign_message_is_queued_without_local_effect_and_only_its_owner_can_settle(string mode)
    {
        using var f = new EngineFixture(); f.SeedSession("sender");
        Assert.True(f.Apply(new RegisterForeignSession("source", "p", "T"), "recipient", actor: Peer()).Applied);
        Assert.True(f.Apply(new Assembly.Engine.Messages.SendMessage("sender", "recipient", "Raw body", mode), "message", actor: new(ActorKind.Session, "sender")).Applied);
        Assert.Equal("queued", f.State("engine_message", "message")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal(0, f.Number("SELECT cancel_requested FROM engine_session WHERE id='recipient'"));
        var outcome = new Assembly.Engine.Messages.MessageOutcome("delivered_to_turn", "native-turn", null);
        Assert.Equal("forbidden", f.Apply(outcome, "message", 1, actor: Peer("other")).Error);
        Assert.Equal("forbidden", f.Apply(outcome, "message", 1, actor: new(ActorKind.System, "adapter")).Error);
        Assert.True(f.Apply(outcome, "message", 1, actor: Peer()).Applied);
        Assert.Equal("delivered_to_turn", f.State("engine_message", "message"));
        Assert.Equal("foreign", f.State("engine_session", "recipient")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Peer_message_requires_source_stub_and_local_recipient()
    {
        using var f = new EngineFixture(); f.SeedSession("recipient");
        Assert.True(f.Apply(new RegisterForeignSession("source", "p", "T"), "sender", actor: Peer()).Applied);
        var message = new Assembly.Engine.Messages.SendMessage("sender", "recipient", "Hello", "prompt");
        Assert.Equal("forbidden", f.Apply(message, "wrong", actor: Peer("other")).Error);
        Assert.Equal("forbidden", f.Apply(message, "forged", actor: new(ActorKind.Session, "sender")).Error);
        Assert.True(f.Apply(message, "message", actor: Peer()).Applied);
        Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public void Old_start_json_and_effect_bytes_remain_identical()
    {
        using var f = new EngineFixture();
        var start = new StartSession("p", null, "/target", "T", null, null, null);
        const string old = "{\"provider\":\"p\",\"model\":null,\"folder\":\"/target\",\"title\":\"T\",\"parentId\":null,\"runId\":null,\"taskUrl\":null}";
        Assert.Equal(old, JsonSerializer.Serialize(start, EngineJson.Default.StartSession));
        Assert.Equal("{\"kind\":\"start_session\"," + old[1..], JsonSerializer.Serialize<EngineCommand>(start, EngineJson.Default.EngineCommand));
        Assert.True(f.Apply(start, "child").Applied);
        Assert.Equal(old, f.Text("SELECT payload_json FROM engine_outbox"));
    }
}
