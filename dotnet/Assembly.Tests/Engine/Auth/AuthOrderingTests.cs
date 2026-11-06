using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthOrderingTests
{
    [Fact]
    public void Same_provider_cancel_waits_for_begin_while_another_account_and_session_can_claim()
    {
        using var f = new EngineFixture(); f.SeedSession("ordinary/%_/session");
        Seed(f, "begin", "begin_sign_in", "provider/codex/account-A");
        Seed(f, "cancel", "cancel_sign_in", "provider/codex/account-A");
        Seed(f, "other", "begin_sign_in", "provider/codex/account-B");
        Seed(f, "session", "stop", "ordinary/%_/session", "ordinary/%_/session");
        Assert.True(Claim(f, "begin").Applied);
        Assert.False(Claim(f, "cancel").Applied);
        Assert.Equal("pending", f.State("engine_outbox", "cancel"));
        Assert.True(Claim(f, "other").Applied);
        Assert.True(Claim(f, "session").Applied);
        f.Db.Writer.Write("test.ordered-finish", s => s.Execute("UPDATE engine_outbox SET state='done' WHERE id='begin'"));
        Assert.True(Claim(f, "cancel").Applied);
    }

    [Fact]
    public void Same_sequence_uses_row_insertion_order_and_unresolved_unknown_blocks()
    {
        using var f = new EngineFixture();
        Seed(f, "z-first", "probe_provider", "provider/codex/A", state: "uncertain");
        Seed(f, "a-second", "begin_sign_in", "provider/codex/A");
        Assert.False(Claim(f, "a-second").Applied);
        Assert.True(Claim(f, "z-first").Applied);
        Assert.False(Claim(f, "a-second").Applied);
        f.Db.Writer.Write("test.known-failure", s => s.Execute("UPDATE engine_outbox SET state='failed' WHERE id='z-first'"));
        Assert.True(Claim(f, "a-second").Applied);
    }

    [Fact]
    public void Externally_owned_parent_need_is_neither_claimed_nor_an_earlier_generic_blocker()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Seed(f, "parent", "parent_need", "s", "s", "uncertain");
        Seed(f, "stop", "stop", "s", "s");
        var refused = Claim(f, "parent");
        Assert.False(refused.Applied); Assert.Equal("forbidden", refused.Error);
        Assert.True(Claim(f, "stop").Applied);
        Assert.Equal("uncertain", f.State("engine_outbox", "parent"));
    }

    [Fact]
    public void Journal_persists_actual_allocated_fallback_and_exact_session_key_without_changing_ids()
    {
        using var f = new EngineFixture(); f.SeedSession("s/%_literal/");
        var command = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var c = new CommandContext(command, f.Machine, new(ActorKind.System, "test"), "entity", 0, 0, 1000);
        f.Db.Writer.Write("test.key-allocation", s => EngineJournal.Commit(s, c, 0, 0, [],
        [new("discarded-old-draft", "opaque", null, null, JsonSerializer.SerializeToElement(new { ordinary = "exact" })),
         new("kept-answer", "answer", "s/%_literal/", null, JsonSerializer.SerializeToElement(new { ordinary = "exact" }))]));
        Assert.Equal("effect/0123456789abcdef0123456789abcdef-0", f.Text("SELECT order_key FROM engine_outbox WHERE id='0123456789abcdef0123456789abcdef-0'"));
        Assert.Equal("s/%_literal/", f.Text("SELECT order_key FROM engine_outbox WHERE id='kept-answer'"));
        Assert.Equal("{\"ordinary\":\"exact\"}", f.Text("SELECT payload_json FROM engine_outbox WHERE id='kept-answer'"));
        Assert.Equal(0, f.Number("SELECT SUM(version+attempt) FROM engine_outbox"));
        var effect = EffectTransitions.Read(f.Db, "kept-answer", default)!;
        Assert.Equal("kept-answer", effect.Id);
    }

    private static CommandResult Claim(EngineFixture f, string id) => f.Apply(new ClaimEffect(), id,
        f.Number("SELECT version FROM engine_outbox WHERE id=$id", new SqlArg("$id", id)), actor: new(ActorKind.System, "engine.worker"));

    private static void Seed(EngineFixture f, string id, string kind, string key, string? session = null, string state = "pending") =>
        f.Db.Writer.Write("test.auth-order", s => s.Execute("""
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,run_id,kind,payload_json,state,attempt,order_key)
            VALUES($id,0,$machine,1000,1000,1,$session,NULL,$kind,'{}',$state,0,$key)
            """, new("$id", id), new("$machine", f.Machine), new("$session", session), new("$kind", kind), new("$state", state), new("$key", key)));
}
