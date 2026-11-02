using Assembly.Engine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class HelperPromptPreparationTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(40000)]
    public void Both_canonical_branches_freeze_complete_body_including_nul_and_task_suffix(int size)
    {
        var title = new string('x', size) + "\0AFTER_NUL😀";
        var text = $"Session \"{title}\" (caller) started you.\nTASK_SUFFIX";
        var command = new SendMessage("caller", "child", text, "prompt");
        var prepared = PreparedHelperPrompt.Prepare(command, title, 7);
        Assert.Equal(Encoding.UTF8.GetBytes(text), prepared.BodyUtf8);
        Assert.Equal(Encoding.UTF8.GetBytes(title), prepared.CallerTitleUtf8);
        Assert.Equal(7, prepared.CallerEpoch);
        var canonical = JsonSerializer.SerializeToUtf8Bytes<EngineCommand>(command, EngineJson.Default.EngineCommand);
        if (size == 10)
        {
            Assert.False(prepared.Oversized);
            Assert.Equal(Encoding.UTF8.GetString(canonical), prepared.CanonicalJournal);
        }
        else
        {
            Assert.True(prepared.Oversized);
            using var envelope = JsonDocument.Parse(prepared.CanonicalJournal);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(canonical)), envelope.RootElement.GetProperty("commandSha256").GetString());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title))), envelope.RootElement.GetProperty("callerTitleSha256").GetString());
            Assert.Equal(canonical.LongLength, envelope.RootElement.GetProperty("commandUtf8Bytes").GetInt64());
            Assert.InRange(Encoding.UTF8.GetByteCount(prepared.CanonicalJournal), 1, 512);
        }
    }
    [Fact]
    public void Actual_large_helper_receipt_replay_precedes_changed_caller_facts_and_hidden_body_conflicts()
    {
        using var f = new EngineFixture(); f.SeedSession("caller", epoch: 7); f.SeedSession("child", state: "running");
        var title = new string('t', 40000) + "\0original";
        Assert.True(f.Db.Writer.Write("title", scope => new EngineCommands(f.Machine, f.Limits).ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "test"), "caller", 0, 7, 1000),
            EngineCommands.PrepareSessionTitle(new Assembly.Engine.Sessions.ReportSessionTitle(title)))).Applied);
        var command = new SendMessage("caller", "child", title + "\0TASK_SUFFIX", "prompt");
        var prepared = PreparedHelperPrompt.Prepare(command, title, 7);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "caller"), "m", 0, 0, 1000);
        CommandResult Apply(PreparedHelperPrompt prompt, CommandContext ctx) => f.Db.Writer.Write("helper", scope =>
            new EngineCommands(f.Machine, f.Limits).ApplyPreparedHelperPrompt(scope, ctx, prompt));
        var first = Apply(prepared, context); Assert.True(first.Applied); Assert.Equal(1, f.Count("engine_message"));
        Assert.True(f.Apply(new Assembly.Engine.Sessions.ReportSessionTitle("renamed"), "caller", 1, 7, new(ActorKind.System, "test")).Applied);
        Assert.Equal(first, Apply(prepared, context));
        Assert.Equal("command_id_conflict", Apply(PreparedHelperPrompt.Prepare(command with { Text = command.Text + "B" }, title, 7), context).Error);
        Assert.Equal("stale_caller", Apply(prepared, context with { CommandId = Guid.NewGuid(), EntityId = "another" }).Error);
        Assert.Equal("forbidden", Apply(prepared, context with { CommandId = Guid.NewGuid(), Actor = new(ActorKind.System, "machine:remote") }).Error);
        f.Reopen(); Assert.Equal(first, Apply(prepared, context)); Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40000)]
    public void Actual_receipt_replays_before_changed_contact_caller_limit_and_expiry(int size)
    {
        using var f = new EngineFixture(); f.SeedSession("caller", epoch: 7); f.SeedSession("child", state: "starting", parentId: "caller", epoch: 3);
        var body = "[message from copied]\n" + new string('é', size) + "\0ORIGINAL_SUFFIX";
        var command = new SendMessage("caller", "child", body, "prompt");
        var prepared = PreparedHelperPrompt.Prepare(command, "Test", 7);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "caller"), "m", 0, 3, 1000);
        var limit = 20; var minutes = 30; var capReads = 0; var expiryReads = 0;
        var commands = new EngineCommands(f.Machine, f.Limits, () => { capReads++; return limit; },
            expiryPolicy: new(_ => { expiryReads++; return minutes; }));
        CommandResult Apply(PreparedHelperPrompt value, CommandContext ctx) => f.Db.Writer.Write("prepared", s => commands.ApplyPreparedHelperPrompt(s, ctx, value));
        var first = Apply(prepared, context); Assert.True(first.Applied);
        var resultBytes = f.Text("SELECT result_json FROM engine_command_receipt");
        var originalDeadline = f.Number("SELECT expires_ms FROM engine_message");
        f.Db.Writer.Write("changed-facts", s =>
        {
            s.Execute("UPDATE engine_session SET title='changed',epoch=99 WHERE id='caller'");
            return s.Execute("UPDATE engine_session SET parent_id=NULL,state='ended',epoch=4 WHERE id='child'");
        });
        limit = 0; minutes = 60;
        Assert.Equal(first, Apply(prepared, context with { NowMs = 9000000 }));
        Assert.Equal(1, capReads); Assert.Equal(1, expiryReads);
        foreach (var changedBody in new[] { body + "last-byte", body.Replace("\0", "", StringComparison.Ordinal), body.Replace("ORIGINAL_SUFFIX", "CHANGED_SUFFIX", StringComparison.Ordinal) })
            Assert.Equal("command_id_conflict", Apply(PreparedHelperPrompt.Prepare(command with { Text = changedBody }, "Test", 7), context).Error);
        Assert.Equal(resultBytes, f.Text("SELECT result_json FROM engine_command_receipt"));
        Assert.Equal(originalDeadline, f.Number("SELECT expires_ms FROM engine_message")); Assert.Equal(0, f.Count("engine_outbox"));
        f.Reopen(); Assert.Equal(first, Apply(prepared, context));
        Assert.Equal(Encoding.UTF8.GetBytes(body), f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message", r => r.Blob(0), default));
        f.Db.Writer.Write("current-facts", s => s.Execute("UPDATE engine_session SET state='starting',parent_id='caller' WHERE id='child'"));
        var fresh = PreparedHelperPrompt.Prepare(command, "changed", 99);
        var freshContext = context with { CommandId = Guid.NewGuid(), EntityId = "fresh", Epoch = 4, NowMs = 2000 };
        Assert.Equal("message_limit", Apply(fresh, freshContext).Error);
        Assert.Equal(2, capReads); Assert.Equal(1, expiryReads);
        limit = 2;
        Assert.True(Apply(fresh, freshContext with { CommandId = Guid.NewGuid(), EntityId = "accepted-fresh" }).Applied);
        Assert.Equal(3602000, f.Number("SELECT expires_ms FROM engine_message WHERE id='accepted-fresh'"));
        Assert.Equal(3, capReads); Assert.Equal(2, expiryReads);
    }

}
