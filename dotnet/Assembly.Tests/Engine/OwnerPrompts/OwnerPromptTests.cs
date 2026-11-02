using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;

namespace Assembly.Tests.Engine.OwnerPrompts;

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
        var result = ApplyPrepared(f, context, command);
        Assert.True(result.Applied, result.Error);
        Assert.Equal(1, result.Version); // Busy recipient cannot steer; acceptance remains queued.
        Assert.Equal(7, result.Epoch);
        Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal("prompt/s/C", f.Text("SELECT id FROM engine_message"));
        Assert.Equal("Exact original text", f.Text("SELECT text FROM engine_message"));
        Assert.Equal(0, f.Count("engine_outbox"));
        var events = f.Count("engine_event");
        f.Reopen();
        Assert.Equal(result, ApplyPrepared(f, context with { NowMs = 3000 }, command));
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
        var result = ApplyPrepared(f, Context(f, command) with { Actor = new(kind, id) }, command);
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
        var accepted = ApplyPrepared(f, context, command);
        Assert.True(accepted.Applied, accepted.Error);
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        Assert.Equal("client_id_conflict", ApplyPrepared(f, context, command with { Text = "Changed" }).Error);
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
        Assert.Equal(accepted, ApplyPrepared(f, context, command));
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
    public void Generated_manifest_and_chunks_keep_original_epoch_and_ordered_attachments(long? targetEpoch)
    {
        var command = new SendOwnerPrompt("s", "Original\nAttached image: /upload/s/one.png",
            "C", ["/upload/s/one.png", "/upload/s/two.jpg"], targetEpoch);
        var prepared = OwnerPromptFingerprint.Prepare("engine-machine", command, default);
        var manifest = new OwnerPromptManifest("C", prepared.ContentSha256, "Original", false,
            prepared.TextUtf8Bytes, prepared.AttachmentJsonUtf8Bytes, prepared.AttachmentCount, targetEpoch, 7, "queued", 4);
        var page = new OwnerPromptPage(prepared.Machine, [manifest], null, null);
        var reopened = JsonSerializer.Deserialize(JsonSerializer.Serialize(page, SessionsJson.Default.OwnerPromptPage), SessionsJson.Default.OwnerPromptPage)!;
        Assert.Equal("engine-machine", reopened.EngineMachine); // Paired application machine identity is separate.
        var item = Assert.Single(reopened.Items);
        Assert.Equal(targetEpoch, item.TargetEpoch); Assert.Equal(7, item.HeldEpoch); Assert.False(item.TextComplete);
        var bytes = System.Text.Encoding.UTF8.GetBytes(prepared.AttachmentIdsJson);
        var chunk = new OwnerPromptChunk(prepared.ContentSha256, "attachments", 0, bytes.LongLength, bytes, null);
        var decoded = JsonSerializer.Deserialize(JsonSerializer.Serialize(chunk, SessionsJson.Default.OwnerPromptChunk), SessionsJson.Default.OwnerPromptChunk)!;
        var references = JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(decoded.Data), SessionsJson.Default.IReadOnlyListString)!;
        Assert.Equal(command.AttachmentIds, references);
        var request = new OwnerPromptRequest(command.ToSessionId, command.Text, item.ClientId, references, item.TargetEpoch);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, SessionsJson.Default.OwnerPromptRequest), SessionsJson.Default.OwnerPromptRequest)!;
        Assert.Equal(targetEpoch, restored.Epoch); Assert.Equal(command.Text, restored.Text);
        Assert.Equal(command.AttachmentIds, restored.AttachmentIds);
    }

    private static CommandResult ApplyPrepared(EngineFixture f, CommandContext context, SendOwnerPrompt command)
    {
        var prepared = OwnerPromptFingerprint.Prepare(f.Machine, command, default);
        return f.Db.Writer.Write("test.owner-prepared", scope =>
            new global::Assembly.Engine.EngineCommands(f.Machine, f.Limits).ApplyOwnerPrompt(scope, context, prepared));
    }

    private static CommandContext Context(EngineFixture f, SendOwnerPrompt command) => new(
        OwnerPromptIdentity.CommandId(f.Machine, command.ToSessionId, command.ClientId), f.Machine, Owner,
        OwnerPromptIdentity.MessageId(command.ToSessionId, command.ClientId), 0, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds());
}
