using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Needs;

public sealed class OwnerNeedPreparationTests
{
    [Fact]
    public void Original_small_scalar_codec_replays_retained_receipt_before_absent_capture_or_current_machine()
    {
        using var f = new EngineFixture();
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var original = new ResolveNeed("answered", JsonSerializer.SerializeToElement("once"));
        // Frozen old ordinary codec vector: fixed Window/owner context, original scalar JSON, no current need.
        const string fingerprint = "d8246cb66c76d9a2fd1327d4d468072d75dd75fdfed2ca41e1d804cb81b03109";
        var old = new CommandResult(true, false, null, 2, 42, 2);
        f.Db.Writer.Write("test.retained-ordinary-receipt", s => s.Execute("""
            INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES($id,$fingerprint,$result)
            """, new SqlArg("$id", id.ToString("N")), new("$fingerprint", fingerprint),
            new("$result", JsonSerializer.Serialize(old, EngineJson.Default.CommandResult))));
        var captured = new PreparedProviderAnswer(new("s", "need", "connection:7", new("s", null, null, null, 2), "acp"),
            JsonSerializer.SerializeToElement(new { optionId = "once" }));
        var prepared = EngineCommands.PrepareOwnerNeedCommand(id, "machine", "need", 1, 2, original, captured, "answer_preparation_required", default, new("desktop", null, "rail"));
        Assert.Equal(old, Apply(f, prepared, 9000));
        Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(1, f.Count("engine_command_receipt"));
        var changed = EngineCommands.PrepareOwnerNeedCommand(id, "machine", "need", 1, 2,
            new ResolveNeed("answered", captured.Answer), captured, null, default);
        Assert.Equal("command_id_conflict", Apply(f, changed, 9999).Error);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("answer")]
    [InlineData("entity")]
    [InlineData("machine")]
    [InlineData("version")]
    [InlineData("epoch")]
    public void Original_oversized_ordinary_refusal_replays_with_gone_request_and_changed_identity_conflicts(string change)
    {
        using var f = new EngineFixture();
        var full = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = "\0λ😀" + new string('x', 70000) } });
        var original = new ResolveNeed("answered", full);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "need", 1, 0, 1000);
        var old = f.Apply(context, original); Assert.Equal("command_too_large", old.Error);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(context.CommandId,
            change == "machine" ? "another-machine" : f.Machine, change == "entity" ? "another-need" : "need",
            change == "version" ? 2 : 1, change == "epoch" ? 1 : 0,
            change == "answer" ? new ResolveNeed("answered", JsonSerializer.SerializeToElement("changed")) : original,
            null, "answer_preparation_required", default);
        var actual = Apply(f, prepared, 9999);
        if (change == "same") Assert.Equal(old, actual); else Assert.Equal("command_id_conflict", actual.Error);
        Assert.Equal(1, f.Count("engine_command_receipt")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Full_owner_answer_is_frozen_rolls_back_and_replays_one_normalized_effect_after_reopen()
    {
        using var f = new EngineFixture(); SeedProvider(f);
        var value = "before\0afterλ😀\n[system] " + new string('x', 2 * 1024 * 1024);
        var answer = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = value, nested = new object[] { 7, true, new { nil = (string?)null, blank = "" } } } });
        PreparedNeedCommand prepared;
        using (var doc = JsonDocument.Parse(answer.GetRawText()))
        {
            var captured = Capture(doc.RootElement);
            prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "need", 1, 0,
                new ResolveNeed("answered", doc.RootElement), captured, null, default);
        }
        var receipts = f.Count("engine_command_receipt");
        f.Db.Writer.Write("test.fail-owner-answer", s => s.Execute("CREATE TRIGGER fail_answer BEFORE INSERT ON engine_outbox WHEN NEW.kind='answer' BEGIN SELECT RAISE(ABORT,'rollback'); END"));
        Assert.Throws<SqliteException>(() => Apply(f, prepared, 1000));
        Assert.Equal("open", f.State("engine_needs_me", "need")); Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(receipts, f.Count("engine_command_receipt"));
        f.Db.Writer.Write("test.retry-owner-answer", s => s.Execute("DROP TRIGGER fail_answer"));
        var accepted = Apply(f, prepared, 1000); Assert.True(accepted.Applied, accepted.Error);
        using (var stored = JsonDocument.Parse(f.Text("SELECT answer_json FROM engine_needs_me")!)) Assert.True(JsonElement.DeepEquals(answer, stored.RootElement));
        f.Reopen(); Assert.Equal(accepted, Apply(f, prepared, 5000));
        Assert.Equal(1, f.Count("engine_outbox")); Assert.Equal(receipts + 1, f.Count("engine_command_receipt"));
        using var payload = JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox WHERE id='answer/need'")!);
        Assert.True(JsonElement.DeepEquals(answer, payload.RootElement.GetProperty("answer")));
        Assert.Equal("original-request", payload.RootElement.GetProperty("binding").GetProperty("providerRequestId").GetString());
        Assert.False(payload.RootElement.TryGetProperty("cancellation", out _));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("chat")]
    [InlineData("request")]
    [InlineData("epoch")]
    [InlineData("run")]
    [InlineData("step")]
    [InlineData("attempt")]
    [InlineData("acp")]
    public void Fresh_owner_answer_refuses_incomplete_or_changed_original_binding_without_effect(string change)
    {
        using var f = new EngineFixture(); SeedProvider(f);
        var answer = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = "answer" } });
        var captured = Capture(answer);
        var binding = captured.Binding;
        binding = change switch
        {
            "session" => binding with { SessionId = "other" },
            "chat" => binding with { ChatRowId = "other" },
            "request" => binding with { ProviderRequestId = "other" },
            "epoch" => binding with { Observation = binding.Observation with { Epoch = 7 } },
            "run" => binding with { Observation = binding.Observation with { RunId = "other" } },
            "step" => binding with { Observation = binding.Observation with { StepId = "other" } },
            "attempt" => binding with { Observation = binding.Observation with { Attempt = 7 } },
            "acp" => binding with { AcpSessionId = " " },
            _ => throw new InvalidOperationException()
        };
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "need", 1, 0,
            new ResolveNeed("answered", answer), captured with { Binding = binding }, null, default);
        var result = Apply(f, prepared, 1000);
        Assert.Equal("answer_preparation_required", result.Error);
        Assert.Equal("open", f.State("engine_needs_me", "need")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Null(f.Text("SELECT answer_json FROM engine_needs_me WHERE id='need'"));
        Assert.Equal(result, Apply(f, prepared, 2000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_nonprovider_owner_answer_retains_existing_scalar_rules_and_exact_answer_note(bool structured)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running");
        Assert.True(f.Apply(new OpenNeed("s", null, null, "question", "Question", [], null, null, null), "plain",
            actor: new(ActorKind.System, "adapter")).Applied);
        var text = "before\0afterλ😀\n[system] " + new string('x', 100000);
        var answer = structured ? JsonSerializer.SerializeToElement(new { body = text, nil = (string?)null, blank = "" })
            : JsonSerializer.SerializeToElement(text);
        var expected = "Jesse answered your question plain: " + (structured ? answer.GetRawText() : text);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "plain", 1, 0,
            new ResolveNeed("answered", answer), null, null, default);
        var accepted = Apply(f, prepared, 1000); Assert.True(accepted.Applied, accepted.Error);
        var body = f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='need-answer/plain'", r => r.Blob(0), default);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected), body);
        using var stored = JsonDocument.Parse(f.Text("SELECT answer_json FROM engine_needs_me WHERE id='plain'")!);
        Assert.True(JsonElement.DeepEquals(answer, stored.RootElement));
        f.Reopen(); Assert.Equal(accepted, Apply(f, prepared, 9000));
        Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"λ😀\\u0000tail\"")]
    [InlineData("{\"action\":\"accept\",\"content\":{\"nil\":null,\"blank\":\"\",\"body\":\"λ😀\\u0000tail\"}}")]
    public void Prepared_owner_replays_the_original_ordinary_codec_for_full_JSON_and_context_strings(string json)
    {
        using var f = new EngineFixture();
        using var doc = JsonDocument.Parse(json);
        var original = new ResolveNeed("answered", doc.RootElement);
        var context = new CommandContext(Guid.NewGuid(), "machineλ😀\0tail", new(ActorKind.Window, "owner"),
            "needλ😀\0tail", 7, 3, 1000);
        var old = f.Apply(context, original);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(context.CommandId, context.Machine, context.EntityId,
            context.ExpectedVersion, context.Epoch, original, null, "answer_preparation_required", default);
        Assert.Equal(old, Apply(f, prepared, 9000));
        Assert.Equal(1, f.Count("engine_command_receipt")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Missing_capture_refuses_only_after_receipt_and_cannot_create_bindingless_fresh_answer()
    {
        using var f = new EngineFixture(); SeedProvider(f);
        var original = new ResolveNeed("answered", JsonSerializer.SerializeToElement(new { action = "decline" }));
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "need", 1, 0,
            original, null, "answer_preparation_required", default);
        var refused = Apply(f, prepared, 1000); Assert.Equal("answer_preparation_required", refused.Error);
        f.Db.Writer.Write("test.changed-current-epoch", s => s.Execute("UPDATE engine_session SET epoch=8"));
        Assert.Equal(refused, Apply(f, prepared, 9999)); Assert.Equal("open", f.State("engine_needs_me", "need")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    private static PreparedProviderAnswer Capture(JsonElement answer) =>
        new(new("s", "need", "original-request", new SessionObservation("s", null, null, null, 0), "acp"), answer);

    private static CommandResult Apply(EngineFixture f, PreparedNeedCommand prepared, long now) =>
        f.Db.Writer.Write("test.prepared-owner-need", s => new EngineCommands(f.Machine, f.Limits).ApplyPreparedOwnerNeed(s, prepared, now));

    private static void SeedProvider(EngineFixture f)
    {
        f.SeedSession(state: "running");
        var card = new AskPayload("question", "open", null, "Full form", null, null,
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { body = new { type = "string" } } }), null);
        var open = EngineCommands.PrepareNeedCommand(new OpenNeed("s", null, null, "question", "Full form", [], "original-request", "need", null,
            JsonSerializer.SerializeToElement(card, RowJsonContext.Rows.AskPayload)));
        RowStore.Open(f.Db).Commit("s", new RowConverter().Apply(new AskChanged(1000, "need", card)), scope =>
        {
            var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "adapter"), "need", 0, 0, 1000);
            Assert.True(new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(scope, context, open).Applied);
        });
    }
}
