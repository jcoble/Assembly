using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Relays;

public sealed class RelayTurnReplyTests
{
    private const string Carrier = "turn-reply-fp/v1/AQAAAAAAAAAAB3BW1pl-OsZB4-S6W0qX92XD1agJ9hk1foW8IFRD63dBEREREREREREREREREREREREREREREREREREREREREREZgkCvInbTydQnF8_sbTjVN_KVATijqUvrucjJwKcH1Q";

    [Fact]
    public void Independent_reply_composition_vectors_and_null_branch_preserve_existing_bytes()
    {
        var evidence = new RelayTurnReplyEvidence("chat-run-1", 7, Carrier);
        Assert.Equal("61dc46bf0775e662523e6b0a26648c6b96a7573fed7ada2d903d33c01b5cc357",
            RelayTurnReplies.Identity(new string('2', 64), evidence));
        Assert.Equal("d77713b4d158c9823817b5ba384ec101003db319bc7452dec8a3111196ec4b3f",
            RelayTurnReplies.Descriptor(new string('3', 64), evidence));
        var actual = RelayDigests.Describe(RelayDigests.Capture("machine-A", "machine-B",
            "turn-reply/2b48e069c5bc1cc6c0cada1aca01cd17de3c9b304dbd74d8f8286d24e5168d71", "session",
            new("child-1", "parent-1", "", "prompt"), "Captured", new(1, "queued", 0, false, 0, "local", null, 0, "machine-B"), evidence));
        Assert.Equal("3bdd320c65d28d7fb281c7e30bb6a22c547c94061ff3987be82207035b00b3bf", actual.IdentitySha256);
        Assert.Equal("64b5f7d9d30ce0a09590f71eb2b51cfa1bd9903e613394b6e6d82adc510676aa", actual.SourceSha256);
        Assert.Equal("3be88be53d98cee8aa5ab6efb1edb0a801826f21fe0d2f8170001073bfd3149b", RelayDigests.DescriptorCommitment(actual));
        Assert.Equal(new string('2', 64), RelayTurnReplies.Identity(new string('2', 64), null));
        Assert.Equal(new string('3', 64), RelayTurnReplies.Descriptor(new string('3', 64), null));
        foreach (var changed in new[] { evidence with { ChatRunId = "different" }, evidence with { TerminalSeq = 8 },
            evidence with { ReceiptFingerprint = Carrier[..^1] + "A" } })
        {
            Assert.NotEqual(RelayTurnReplies.Identity(new string('2', 64), evidence), RelayTurnReplies.Identity(new string('2', 64), changed));
            Assert.NotEqual(RelayTurnReplies.Descriptor(new string('3', 64), evidence), RelayTurnReplies.Descriptor(new string('3', 64), changed));
        }
        var capture = RelayDigests.Capture("source", "target", "message", "session", new("sender", "recipient", "Hello", "prompt"),
            "Captured", new(1, "queued", 0, false, 0, "local", null, 0, "target"));
        var descriptor = RelayDigests.Describe(capture);
        Assert.DoesNotContain("replyEvidence", JsonSerializer.Serialize(descriptor));
        Assert.DoesNotContain("ReplyEvidence", JsonSerializer.Serialize(descriptor));
        Assert.Equal("ef03753ff608cd275b486a4001795f3b179438bfd5ad0d43c42a44071bdf7173", capture.IdentitySha256);
        Assert.Equal("cdd1ac0dccba33e62788bad4c4f904ac217b1e2306efcd06f9e8e50e4965fb53", RelayDigests.DescriptorCommitment(descriptor));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("世界😀\0after_nul")]
    public void Owning_reply_read_recovers_exact_terminal_and_carrier_after_end_rename_reopen_and_event_compaction(string body)
    {
        using var f = Source(body, out var id);
        var capture = new EngineReader(f.Db, f.Machine).RelaySource(new(id, "target"), default);
        Assert.Equal(body, capture.Identity.Command.Text);
        Assert.Equal("Test (child)", capture.SenderLabel);
        var evidence = Assert.IsType<RelayTurnReplyEvidence>(capture.ReplyEvidence);
        Assert.Equal("chat-run", evidence.ChatRunId); Assert.Equal(3, evidence.TerminalSeq);
        Assert.Equal(f.Text("SELECT fingerprint FROM engine_command_receipt WHERE command_id LIKE 'turn-reply/v1/%'"), evidence.ReceiptFingerprint);
        var descriptor = RelayDigests.Describe(capture);
        Assert.Equal(evidence, descriptor.ReplyEvidence);
        f.Db.Writer.Write("test.after", s =>
        {
            s.Execute("UPDATE engine_session SET title='renamed',state='ended' WHERE id='child'");
            s.Execute("DELETE FROM engine_event");
            return 0;
        });
        f.Reopen();
        var reopened = new EngineReader(f.Db, f.Machine).RelaySource(new(id, "target"), default);
        Assert.Equal(capture, reopened);
        Assert.Equal(descriptor, RelayDigests.Describe(reopened));
        Assert.Equal(body, PreparedRelaySend.Create(descriptor, RelayHash.Utf8.GetBytes(body), RelayHash.Utf8.GetBytes(capture.SenderLabel)).Command.Text);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("carrier")]
    [InlineData("terminal")]
    [InlineData("run")]
    [InlineData("body")]
    [InlineData("parent")]
    [InlineData("result")]
    [InlineData("row_body")]
    public void Missing_or_corrupt_actual_reply_provenance_never_becomes_ordinary_send(string damage)
    {
        using var f = Source("original", out var id);
        f.Db.Writer.Write("test.damage", s => s.Execute(damage switch
        {
            "receipt" => "DELETE FROM engine_command_receipt WHERE command_id LIKE 'turn-reply/v1/%'",
            "carrier" => "UPDATE engine_command_receipt SET fingerprint='corrupt' WHERE command_id LIKE 'turn-reply/v1/%'",
            "terminal" => "DELETE FROM rows WHERE kind='turn' AND seq=3",
            "run" => "UPDATE rows SET run_id='wrong' WHERE kind='turn' AND seq=3",
            "body" => "UPDATE engine_message SET text='changed'",
            "row_body" => "UPDATE rows SET payload='{\"blocks\":false}' WHERE kind='message'",
            "result" => "UPDATE engine_command_receipt SET result_json='{}' WHERE command_id LIKE 'turn-reply/v1/%'",
            _ => "UPDATE engine_session SET parent_id=NULL WHERE id='child'"
        }));
        Assert.Throws<RelayRefused>(() => new EngineReader(f.Db, f.Machine).RelaySource(new(id, "target"), default));
    }

    [Fact]
    public void Full_large_reply_and_label_keep_source_provenance()
    {
        var body = new string('界', 1500000) + "\0AFTER_BODY";
        var title = new string('名', 400000) + "\0AFTER_LABEL";
        using var f = Source(body, out var id, title);
        var capture = new EngineReader(f.Db, f.Machine).RelaySource(new(id, "target"), default);
        Assert.Equal(body, capture.Identity.Command.Text); Assert.Equal(title + " (child)", capture.SenderLabel);
        var descriptor = RelayDigests.Describe(capture);
        Assert.Equal(RelayHash.Utf8.GetByteCount(body), descriptor.Body.Utf8Bytes);
        Assert.Equal(RelayHash.Utf8.GetByteCount(capture.SenderLabel), descriptor.SenderLabel.Utf8Bytes);
        Assert.Equal(descriptor, PreparedRelaySend.Create(descriptor, RelayHash.Utf8.GetBytes(body), RelayHash.Utf8.GetBytes(capture.SenderLabel)).Source);
    }

    [Fact]
    public void Ordinary_command_can_use_reply_shaped_id_but_cannot_synthesize_provenance()
    {
        using var f = new EngineFixture();
        Assert.True(f.Apply(new RegisterForeignSession("target", "codex", "Parent"), "parent", actor: new(ActorKind.System, "machine:target")).Applied);
        f.SeedSession("child", parentId: "parent");
        var id = "turn-reply/" + new string('a', 64);
        Assert.True(f.Apply(new SendMessage("child", "parent", "ordinary", "prompt"), id, actor: new(ActorKind.Session, "child")).Applied);
        Assert.Throws<RelayRefused>(() => new EngineReader(f.Db, f.Machine).RelaySource(new(id, "target"), default));
        Assert.Equal(1, f.Count("engine_message"));
    }

    internal static EngineFixture Source(string body, out string id, string? title = null, string target = "target")
    {
        var f = new EngineFixture();
        id = CreateReply(f, body, target, title);
        return f;
    }

    internal static string CreateReply(EngineFixture f, string body, string target, string? title = null)
    {
        Assert.True(f.Apply(new RegisterForeignSession(target, "codex", "Parent"), "parent", actor: new(ActorKind.System, "machine:" + target)).Applied);
        f.SeedSession("child", parentId: "parent");
        if (title is not null) f.Db.Writer.Write("test.title", scope => scope.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id='child'", new SqlArg("$title", RelayHash.Utf8.GetBytes(title))));
        var batch = new RowBatch();
        batch.Rows.Add(new("chat-run", null, "turn", JsonDocument.Parse("{\"state\":\"started\"}").RootElement.Clone(), 1000));
        batch.Rows.Add(new("chat-run", "assistant", "message", JsonSerializer.SerializeToElement(new { blocks = new[] { new { value = body } } }), 1234));
        batch.Rows.Add(new("chat-run", null, "turn", JsonDocument.Parse("{\"state\":\"finished\"}").RootElement.Clone(), 1234));
        RowStore.Open(f.Db).Commit("child", batch, scope =>
        {
            var prepared = EngineCommands.PrepareTurnReply(new("child", null, null, null, 0), new("chat-run", null, null, false, 1234),
                RowStore.TerminalReply(scope, "child", "chat-run")!);
            Assert.True(new EngineCommands(f.Machine, f.Limits).ApplyTurnReply(scope, prepared, 2000).Applied);
        });
        return f.Text("SELECT id FROM engine_message")!;
    }
}
