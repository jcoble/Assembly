using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Preferences;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.Expiry;

public sealed class PreparedHelperExpiryTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(40000)]
    public void Actual_prepared_helper_snapshots_same_writer_policy_and_preserves_full_bytes_replay_and_release(int size)
    {
        using var f = new EngineFixture(); f.SeedSession("caller", epoch: 7); f.SeedSession("child", "running", parentId: "caller");
        _ = new PreferenceStore(f.Db);
        var commands = new EngineCommands(f.Machine, f.Limits, expiryPolicy: new(MessageExpiryPreferences.ReadMinutes));
        var title = new string('t', size) + "\0AFTER_NUL😀";
        Assert.True(f.Db.Writer.Write("test.title", s => commands.ApplyPreparedSessionTitle(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "observer"), "caller", 0, 7, 1000),
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        var text = $"Session \"{title}\" (caller) started you.\nTASK_SUFFIX";
        var command = new SendMessage("caller", "child", text, "prompt");
        var prepared = PreparedHelperPrompt.Prepare(command, title, 7);
        Assert.Equal(size > 32768, prepared.Oversized);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "caller"), "helper", 0, 0, 1000);
        var first = f.Db.Writer.Write("test.helper", s =>
        {
            s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes','45')");
            var result = commands.ApplyPreparedHelperPrompt(s, context, prepared);
            Assert.True(result.Applied, result.Error);
            Assert.Equal(Encoding.UTF8.GetBytes(text), s.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='helper'", r => r.Blob(0)));
            Assert.Equal(2701000, s.QueryFirst("SELECT expires_ms FROM engine_message WHERE id='helper'", r => r.Long(0)));
            return result;
        });
        f.Reopen();
        f.Db.Writer.Write("test.corrupt", s => s.Execute("UPDATE preferences SET value='not-json' WHERE key='messageExpiryMinutes'"));
        Assert.Equal(first, f.Db.Writer.Write("test.replay", s => commands.ApplyPreparedHelperPrompt(s, context with { NowMs = 9999999 }, prepared)));
        var changed = PreparedHelperPrompt.Prepare(command with { Text = text + "CHANGED_AFTER_NUL" }, title, 7);
        Assert.Equal("command_id_conflict", f.Db.Writer.Write("test.conflict", s => commands.ApplyPreparedHelperPrompt(s, context, changed)).Error);
        Assert.True(f.Apply(new SetSessionState("idle", null, null), "child", actor: new(ActorKind.System, "observer")).Applied);
        var effect = EffectTransitions.Read(f.Db, "send/helper/1", default)!;
        var delivery = effect.Payload.Deserialize(EngineJson.Default.DeliverMessage)!;
        var label = MessageText.Label(title, "caller", "session");
        Assert.Equal(MessageText.Prompt(label, text), delivery.Text); Assert.Equal(label, delivery.SenderLabel);
        Assert.Equal(2701000, f.Number("SELECT expires_ms FROM engine_message WHERE id='helper'"));
        Assert.Equal(1, f.Count("engine_message")); Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40000)]
    public void Prepared_helper_deadline_overflow_rolls_back_the_whole_supplied_writer(int size)
    {
        using var f = new EngineFixture(); f.SeedSession("caller"); f.SeedSession("child", "running", parentId: "caller");
        _ = new PreferenceStore(f.Db);
        var commands = new EngineCommands(f.Machine, f.Limits, expiryPolicy: new(MessageExpiryPreferences.ReadMinutes));
        var title = new string('t', size) + "\0AFTER_NUL";
        Assert.True(f.Db.Writer.Write("test.title", s => commands.ApplyPreparedSessionTitle(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "observer"), "caller", 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        var prepared = PreparedHelperPrompt.Prepare(new("caller", "child", title + "\0TASK_SUFFIX", "prompt"), title, 0);
        var receipts = f.Count("engine_command_receipt"); var events = f.Count("engine_event");
        var error = Assert.Throws<CallFailed>(() => f.Db.Writer.Write("test.overflow", s =>
        {
            s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes','2147483647')");
            return commands.ApplyPreparedHelperPrompt(s, new(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "caller"), "overflow", 0, 0, long.MaxValue - 1), prepared);
        }));
        Assert.Equal("message_expiry_overflow", error.Message);
        Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal(receipts, f.Count("engine_command_receipt")); Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM preferences"));
    }
}
