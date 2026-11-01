using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;

namespace Assembly.Tests.Engine;

public sealed class OwnerPromptTests
{
    private static readonly EngineActor Owner = new(ActorKind.Window, "owner");

    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    public void Owner_creation_uses_message_identity_and_original_nullable_target(long? targetEpoch)
    {
        using var f = new EngineFixture();
        f.SeedSession(state: "running", epoch: 7);
        var command = new SendOwnerPrompt("s", "Exact original text", "C", [], targetEpoch);
        var context = Context(f, command);
        var result = f.Apply(context, command);
        Assert.True(result.Applied, result.Error);
        Assert.Equal(1, result.Version); // Busy recipient cannot steer; acceptance remains queued.
        Assert.Equal(7, result.Epoch);
        Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal("prompt/s/C", f.Text("SELECT id FROM engine_message"));
        Assert.Equal("Exact original text", f.Text("SELECT text FROM engine_message"));
        Assert.Equal(0, f.Count("engine_outbox"));
        var events = f.Count("engine_event");
        f.Reopen();
        Assert.Equal(result, f.Apply(context with { NowMs = 3000 }, command));
        Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(ActorKind.Session, "s")]
    [InlineData(ActorKind.System, "owner")]
    [InlineData(ActorKind.Window, "other")]
    public void Owner_command_refuses_other_actors_without_inserting(ActorKind kind, string id)
    {
        using var f = new EngineFixture(); f.SeedSession();
        var command = new SendOwnerPrompt("s", "Prompt", "C", [], null);
        var result = f.Apply(Context(f, command) with { Actor = new(kind, id) }, command);
        Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, f.Count("engine_message"));
        Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Empty(f.Events());
    }

    [Fact]
    public void Reused_client_changed_payload_conflicts_without_new_effect()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var command = new SendOwnerPrompt("s", "Original", "C", [], null);
        var context = Context(f, command);
        var accepted = f.Apply(context, command);
        Assert.True(accepted.Applied, accepted.Error);
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        Assert.Equal("client_id_conflict", f.Apply(context, command with { Text = "Changed" }).Error);
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
        Assert.Equal(accepted, f.Apply(context, command));
    }

    [Fact]
    public void Stable_owner_identity_separates_machine_session_and_client_components()
    {
        var first = OwnerPromptIdentity.CommandId("machine", "s", "C");
        Assert.Equal(first, OwnerPromptIdentity.CommandId("machine", "s", "C"));
        Assert.NotEqual(first, OwnerPromptIdentity.CommandId("other", "s", "C"));
        Assert.NotEqual(first, OwnerPromptIdentity.CommandId("machine", "other", "C"));
        Assert.NotEqual(first, OwnerPromptIdentity.CommandId("machine", "s", "other"));
        Assert.NotEqual(OwnerPromptIdentity.CommandId("a", "bc", "D"), OwnerPromptIdentity.CommandId("ab", "c", "D"));
        Assert.Equal("prompt/s/C", OwnerPromptIdentity.MessageId("s", "C"));
        Assert.NotEqual(OwnerPromptIdentity.MessageId("s", "C"), OwnerPromptIdentity.MessageId("other", "C"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    public void Generated_replay_round_trip_preserves_original_epoch_and_ordered_attachments(long? targetEpoch)
    {
        var replay = new OwnerPromptReplay("prompt/s/C", "machine", "s", "C", "Original\nAttached image: /upload/s/one.png",
            ["/upload/s/one.png", "/upload/s/two.jpg"], targetEpoch, 7, "queued", 4, null, "workflow");
        var json = JsonSerializer.Serialize(replay, SessionsJson.Default.OwnerPromptReplay);
        var reopened = JsonSerializer.Deserialize(json, SessionsJson.Default.OwnerPromptReplay)!;
        Assert.Equal(replay.TargetEpoch, reopened.TargetEpoch);
        Assert.Equal(7, reopened.HeldEpoch);
        Assert.Equal(replay.AttachmentIds, reopened.AttachmentIds);
        var request = new OwnerPromptRequest(reopened.ToSessionId, reopened.Text, reopened.ClientId, reopened.AttachmentIds, reopened.TargetEpoch);
        var wire = JsonSerializer.Serialize(request, SessionsJson.Default.OwnerPromptRequest);
        var restored = JsonSerializer.Deserialize(wire, SessionsJson.Default.OwnerPromptRequest)!;
        Assert.Equal(targetEpoch, restored.Epoch);
        Assert.Equal(replay.Text, restored.Text);
        Assert.Equal(replay.AttachmentIds, restored.AttachmentIds);
        var command = new SendOwnerPrompt(restored.Session, restored.Text, restored.ClientId, restored.AttachmentIds, restored.Epoch);
        var durable = JsonSerializer.Serialize<EngineCommand>(command, global::Assembly.Engine.EngineJson.Default.EngineCommand);
        var decoded = Assert.IsType<SendOwnerPrompt>(JsonSerializer.Deserialize(durable, global::Assembly.Engine.EngineJson.Default.EngineCommand));
        Assert.Equal(targetEpoch, decoded.TargetEpoch); Assert.Equal(command.AttachmentIds, decoded.AttachmentIds);
    }

    private static CommandContext Context(EngineFixture f, SendOwnerPrompt command) => new(
        OwnerPromptIdentity.CommandId(f.Machine, command.ToSessionId, command.ClientId), f.Machine, Owner,
        OwnerPromptIdentity.MessageId(command.ToSessionId, command.ClientId), 0, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds());
}
