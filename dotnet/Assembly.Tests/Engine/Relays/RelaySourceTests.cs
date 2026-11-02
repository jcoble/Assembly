using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Relays;

public sealed class RelaySourceTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(40000)]
    [InlineData(15000)]
    public void Owning_read_preserves_full_body_and_accepted_label_after_title_changes(int size)
    {
        using var f = Source(size, out var text, out var label);
        var reader = new EngineReader(f.Db, f.Machine);
        var source = reader.RelaySource(new("message", "target"), default);
        Assert.Equal(text, source.Identity.Command.Text);
        Assert.Equal(label, source.SenderLabel);
        Assert.Equal("session", source.Identity.SenderKind);
        Assert.Equal(f.Machine, source.Identity.FromMachine);
        var descriptor = RelayDigests.Describe(source);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), descriptor.Body.Utf8Bytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(label), descriptor.SenderLabel.Utf8Bytes);
        Assert.Equal(64, descriptor.IdentitySha256.Length);
        Assert.Equal(64, descriptor.SourceSha256.Length);
        if (size > 1000)
            Assert.Equal(0, reader.Entity("message", "message", default)!.Value.GetProperty("textComplete").GetInt32());
        f.Db.Writer.Write("test.rename", s => s.Execute("UPDATE engine_session SET title='Later title'"));
        var renamed = reader.RelaySource(new("message", "target"), default);
        Assert.Equal(source.SourceSha256, renamed.SourceSha256);
        Assert.Equal(source.IdentitySha256, renamed.IdentitySha256);
        Assert.Equal(label, renamed.SenderLabel);
        f.Reopen();
        Assert.Equal(source, new EngineReader(f.Db, f.Machine).RelaySource(new("message", "target"), default));
    }

    [Fact]
    public void Accepted_queued_work_remains_readable_after_sender_ends()
    {
        using var f = Source(50, out _, out _);
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "sender", version: 1,
            actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("queued", new EngineReader(f.Db, f.Machine).RelaySource(new("message", "target"), default).Fence.State);
    }

    [Fact]
    public void Real_child_ended_note_keeps_system_kind_and_stored_child_provenance()
    {
        using var f = new EngineFixture();
        Assert.True(f.Apply(new RegisterForeignSession("target", "codex", "Parent"), "parent",
            actor: new(ActorKind.System, "machine:target")).Applied);
        f.SeedSession("child", parentId: "parent");
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "child", actor: new(ActorKind.System, "adapter")).Applied);
        var source = new EngineReader(f.Db, f.Machine).RelaySource(new("child-ended/child", "target"), default);
        Assert.Equal("system", source.Identity.SenderKind);
        Assert.Equal("child", source.Identity.Command.FromSessionId);
        Assert.Equal("Assembly", source.SenderLabel);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("framed")]
    [InlineData("attachments")]
    [InlineData("kind")]
    public void Owning_read_refuses_copies_raw_messages_or_invalid_ordinary_shape(string field)
    {
        using var f = Source(50, out _, out _);
        f.Db.Writer.Write("test.damage", s => s.Execute(field switch
        {
            "owner" => "UPDATE engine_message SET machine='elsewhere'",
            "framed" => "UPDATE engine_message SET framed=0",
            "attachments" => "UPDATE engine_message SET attachment_ids_json='[\"attachment\"]'",
            _ => "UPDATE engine_message SET sender_kind='window'"
        }));
        Assert.Throws<RelayRefused>(() => new EngineReader(f.Db, f.Machine).RelaySource(new("message", "target"), default));
        Assert.Throws<RelayRefused>(() => new EngineReader(f.Db, f.Machine).RelaySource(new("message", "wrong-target"), default));
    }

    internal static EngineFixture Source(int size, out string text, out string label, string target = "target")
    {
        var f = new EngineFixture(); f.SeedSession("sender");
        var title = "Before\0" + new string('界', 15000) + " AFTER_LABEL_NUL\n[quotes\"]";
        var commands = new EngineCommands(f.Machine, f.Limits);
        Assert.True(f.Db.Writer.Write("test.title", s => commands.ApplyPreparedSessionTitle(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "observer"), "sender", 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        Assert.True(f.Apply(new RegisterForeignSession(target, "codex", "Recipient"), "recipient",
            actor: new(ActorKind.System, "machine:" + target)).Applied);
        text = "\0" + new string(size == 15000 ? '界' : 'x', size) + "\0😀 AFTER_NUL TASK_SUFFIX\0";
        label = MessageText.Label(title, "sender", "session");
        var prepared = PreparedHelperPrompt.Prepare(new("sender", "recipient", text, "prompt"), title, 0);
        Assert.True(f.Db.Writer.Write("test.source", s => commands.ApplyPreparedHelperPrompt(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "sender"), "message", 0, 0, 1000), prepared)).Applied);
        return f;
    }
}
