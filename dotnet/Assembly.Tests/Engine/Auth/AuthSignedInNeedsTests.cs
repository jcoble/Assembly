using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthSignedInNeedsTests
{
    [Fact]
    public void Success_closes_all_matching_needs_with_actual_operation_lineage_not_only_origin()
    {
        using var f = new Fixture(); f.Need("s", 1); f.Need("s", 2, inserted: true); f.Session("other"); f.Need("other", 1); f.Begin("sign-in/s/1");
        Assert.True(f.Success().Applied); foreach (var id in new[] { "sign-in/s/1", "sign-in/s/2", "sign-in/other/1" }) f.AssertClosed(id);
        Assert.Equal("operation", f.F.Text("engine_sign_in", "operation", "id")); Assert.Equal(0, f.Core.Count("engine_message"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'")); Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='probe_provider'"));
        Assert.Equal("idle", f.Core.State("engine_session", "s")); Assert.Equal("idle", f.Core.State("engine_session", "other"));
    }

    [Fact]
    public void One_authoritative_success_closes_a_large_matching_set_without_a_silent_cap()
    {
        using var f = new Fixture(); f.Need("s", 1);
        for (var i = 0; i < 125; i++) { var session = "batch-" + i; f.Session(session); f.Need(session, 1); }
        f.Begin(); Assert.True(f.Success().Applied);
        Assert.Equal(126, f.Core.Number("SELECT COUNT(*) FROM engine_needs_me WHERE state='answered' AND answered_by='sign-in/operation' AND answer_json='\"signed_in\"'"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_session WHERE state='needs_me'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='probe_provider'"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Fact]
    public void Owner_need_start_retains_its_generated_actual_operation_id_in_terminal_lineage()
    {
        using var f = new Fixture(); f.Need("s", 1);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Core.Machine, "sign-in/s/1", 1, 0,
            new("answered", JsonSerializer.SerializeToElement("sign_in")), null, null, default);
        var started = f.Core.Db.Writer.Write("test.owner-auth-start", scope => f.F.Commands.ApplyPreparedOwnerNeed(scope, prepared, 1000)); Assert.True(started.Applied, started.Error);
        var operation = f.Core.Text("SELECT id FROM engine_sign_in")!; Assert.NotEqual("operation", operation);
        Assert.Equal(2, f.F.Version("engine_needs_me", "sign-in/s/1")); Assert.Equal("open", f.Core.State("engine_needs_me", "sign-in/s/1"));
        Assert.True(f.F.Apply(new ClaimEffect(), "begin_sign_in/" + operation, 0, new(ActorKind.System, "engine.worker")).Applied);
        var binding = new SignInAttemptBinding(operation, "codex/", "begin_sign_in/" + operation, 1);
        Assert.True(f.F.Apply(new SignInProgress("finishing", null, binding), operation, 1, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.True(f.F.Apply(new SignInProgress("signed_in", null, binding), operation, 2, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.Equal("answered", f.Core.State("engine_needs_me", "sign-in/s/1")); Assert.Equal(3, f.F.Version("engine_needs_me", "sign-in/s/1"));
        Assert.Equal("sign-in/" + operation, f.F.Text("engine_needs_me", "sign-in/s/1", "answered_by"));
        Assert.Equal(0, f.Core.Count("engine_message")); Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Fact]
    public void Child_need_closure_adds_no_second_parent_notice_or_ordinary_answer_message()
    {
        using var f = new Fixture(); f.Session("parent"); f.Core.SeedSession("child", parentId: "parent"); f.Need("child", 1);
        var before = f.Core.Text("SELECT json_group_array(json_array(id,version,text,state,to_session_id)) FROM engine_message WHERE to_session_id='parent'");
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_message WHERE to_session_id='parent'")); f.Begin(); Assert.True(f.Success().Applied); f.AssertClosed("sign-in/child/1");
        Assert.Equal(before, f.Core.Text("SELECT json_group_array(json_array(id,version,text,state,to_session_id)) FROM engine_message WHERE to_session_id='parent'"));
        Assert.Equal(1, f.Core.Count("engine_message")); Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Fact]
    public void Direct_provider_login_without_origin_still_closes_the_matching_confirmation()
    {
        using var f = new Fixture(); f.Need("s", 1); f.Begin(); Assert.True(f.Success().Applied); f.AssertClosed("sign-in/s/1");
    }

    [Theory]
    [InlineData("account")]
    [InlineData("provider")]
    [InlineData("machine")]
    [InlineData("holder")]
    [InlineData("reserved")]
    [InlineData("ended")]
    [InlineData("ordinary")]
    [InlineData("malformed")]
    public void Other_targets_nonlocal_owners_and_nonstructural_needs_are_unchanged(string difference)
    {
        using var f = new Fixture(); f.Need("s", 1); f.Session("excluded"); f.Need("excluded", 1);
        f.Core.Db.Writer.Write("test.ineligible-auth-need", s => s.Execute(difference switch
        {
            "account" => "UPDATE engine_session SET account_context=$account WHERE id='excluded'",
            "provider" => "UPDATE engine_session SET provider='other-provider' WHERE id='excluded'",
            "machine" => "UPDATE engine_session SET machine='peer' WHERE id='excluded'",
            "holder" => "UPDATE engine_session SET holder='peer' WHERE id='excluded'",
            "reserved" => "UPDATE engine_session SET reserved_epoch=1 WHERE id='excluded'",
            "ended" => "UPDATE engine_session SET state='ended' WHERE id='excluded'",
            "ordinary" => "UPDATE engine_needs_me SET kind='question' WHERE session_id='excluded'",
            _ => """UPDATE engine_needs_me SET options_json='["sign_in","dismiss","other"]' WHERE session_id='excluded'"""
        }, new SqlArg("$account", "acct-v1:" + new string('b', 64))));
        var before = f.NeedBytes("sign-in/excluded/1"); f.Begin(); Assert.True(f.Success().Applied); f.AssertClosed("sign-in/s/1");
        Assert.Equal(before, f.NeedBytes("sign-in/excluded/1")); Assert.Equal("open", f.Core.State("engine_needs_me", "sign-in/excluded/1"));
    }

    [Fact]
    public void All_needs_close_before_one_existing_release_reserves_only_the_first_queued_message()
    {
        using var f = new Fixture(); f.Need("s", 1); f.Need("s", 2); f.Send("first"); f.Send("second"); Assert.Equal(0, f.Core.Count("engine_outbox")); f.Begin();
        Assert.True(f.Success().Applied); f.AssertClosed("sign-in/s/1"); f.AssertClosed("sign-in/s/2");
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        Assert.Equal(1, f.Core.Number("SELECT in_flight FROM engine_message WHERE id='first'")); Assert.Equal(0, f.Core.Number("SELECT in_flight FROM engine_message WHERE id='second'"));
        var effect = EffectTransitions.Read(f.Core.Db, "send/first/1", default)!; var body = effect.Payload.Deserialize(EngineJson.Default.DeliverMessage)!;
        Assert.Contains("exact\0body", body.Text); Assert.Equal("first", body.ClientId); Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_message WHERE id LIKE 'need-answer/%'"));
    }

    [Fact]
    public void Existing_inflight_delivery_remains_the_same_reservation_after_need_closure()
    {
        using var f = new Fixture(); f.Send("original"); Assert.True(f.F.Apply(new ClaimEffect(), "send/original/1", 0, new(ActorKind.System, "engine.worker")).Applied);
        f.Need("s", 1); var before = f.Core.Text("SELECT json_array(id,payload_json,state,attempt,order_key) FROM engine_outbox WHERE id='send/original/1'"); f.Begin();
        Assert.True(f.Success().Applied); f.AssertClosed("sign-in/s/1"); Assert.Equal(before, f.Core.Text("SELECT json_array(id,payload_json,state,attempt,order_key) FROM engine_outbox WHERE id='send/original/1'"));
        Assert.Equal(1, f.Core.Number("SELECT in_flight FROM engine_message WHERE id='original'")); Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
    }

    [Fact]
    public void Closure_failure_rolls_back_operation_provider_probe_release_and_receipt_then_exact_retry_succeeds()
    {
        using var f = new Fixture(); f.Need("s", 1); f.Send("queued"); f.Begin(); var before = f.StateBytes();
        f.Core.Db.Writer.Write("test.auth-closure-fault", s => s.Execute("CREATE TRIGGER fail_auth_closure BEFORE UPDATE ON engine_needs_me BEGIN SELECT RAISE(ABORT,'need closure fault'); END"));
        Assert.Throws<SqliteException>(() => f.Success()); Assert.Equal(before, f.StateBytes());
        f.Core.Db.Writer.Write("test.auth-closure-unfault", s => s.Execute("DROP TRIGGER fail_auth_closure")); Assert.True(f.Success().Applied); f.AssertClosed("sign-in/s/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stale_or_cancelled_attempt_cannot_close_a_need(bool cancelled)
    {
        using var f = new Fixture(); f.Need("s", 1); f.Begin(); if (cancelled) Assert.True(f.F.Apply(new CancelSignIn(), "operation", 2).Applied);
        var priorAuth = f.F.Text("engine_provider", "codex/", "auth_state");
        var result = f.Success(expected: cancelled ? 3 : 1); Assert.False(result.Applied); Assert.Equal("open", f.Core.State("engine_needs_me", "sign-in/s/1"));
        Assert.Equal(priorAuth, f.F.Text("engine_provider", "codex/", "auth_state")); Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='probe_provider'"));
    }

    [Fact]
    public void Exact_success_receipt_replay_cannot_close_a_later_auth_generation()
    {
        using var f = new Fixture(); f.Need("s", 1); f.Begin(); var result = f.Success(); Assert.True(result.Applied); f.AssertClosed("sign-in/s/1"); f.Need("s", 2); var before = f.StateBytes();
        Assert.Equal(result, f.Success()); Assert.Equal(before, f.StateBytes()); Assert.Equal("open", f.Core.State("engine_needs_me", "sign-in/s/2"));
        Assert.False(f.Success(fresh: true, expected: 3).Applied); Assert.Equal("open", f.Core.State("engine_needs_me", "sign-in/s/2"));
    }

    [Fact]
    public void Literal_session_wildcards_do_not_capture_another_confirmation_prefix()
    {
        using var f = new Fixture(); f.Session("s%_/"); f.Need("s%_/", 1); f.Need("s", 1); f.Begin(); Assert.True(f.Success().Applied); f.AssertClosed("sign-in/s%_//1"); f.AssertClosed("sign-in/s/1");
    }

    private sealed class Fixture : IDisposable
    {
        internal AuthRecordFixture F { get; } = new((p, a) => p == "codex" && a == "");
        internal EngineFixture Core => F.Core;
        private readonly SignInAttemptBinding _binding = new("operation", "codex/", "begin_sign_in/operation", 1);
        private CommandContext _success = null!;
        internal Fixture() => Session("s");
        internal void Session(string id) => Core.SeedSession(id);
        internal void Need(string session, int number, bool inserted = false)
        {
            var text = "codex needs you to sign in on " + Core.Machine + "." + (inserted ? " Your last message reached codex before sign-in was needed and was not sent again." : "");
            var result = F.Apply(new OpenNeed(session, null, null, "confirmation", text, ["sign_in", "dismiss"], null, null, null), "sign-in/" + session + "/" + number, actor: new(ActorKind.System, "adapter")); Assert.True(result.Applied, result.Error);
        }
        internal void Begin(string? origin = null)
        {
            Assert.True(F.Apply(new StartSignIn("codex", "", null, origin), "operation").Applied);
            Assert.True(F.Apply(new ClaimEffect(), "begin_sign_in/operation", 0, new(ActorKind.System, "engine.worker")).Applied);
            Assert.True(F.Apply(new SignInProgress("finishing", null, _binding), "operation", 1, new(ActorKind.System, "sign_in.effects")).Applied);
            _success = F.Context("operation", 2, new(ActorKind.System, "sign_in.effects"));
        }
        internal CommandResult Success(bool fresh = false, long? expected = null) => F.Apply(_success with { CommandId = fresh ? Guid.NewGuid() : _success.CommandId, ExpectedVersion = expected ?? _success.ExpectedVersion }, new SignInProgress("signed_in", null, _binding));
        internal void Send(string id) => Assert.True(F.Apply(new SendMessage(null, "s", "exact\0body", "prompt"), id).Applied);
        internal void AssertClosed(string id)
        {
            Assert.Equal("answered", Core.State("engine_needs_me", id)); Assert.Equal("\"signed_in\"", F.Text("engine_needs_me", id, "answer_json")); Assert.Equal("sign-in/operation", F.Text("engine_needs_me", id, "answered_by")); Assert.Equal(2, F.Version("engine_needs_me", id));
        }
        internal string? NeedBytes(string id) => Core.Text("SELECT json_array(id,version,state,text,options_json,answer_json,answered_by,answered_ms) FROM engine_needs_me WHERE id=$id", new SqlArg("$id", id));
        internal string StateBytes() => string.Join("|", new[] { "SELECT group_concat(json_array(id,version,state,activity_state)) FROM engine_session", "SELECT group_concat(json_array(id,version,state,answer_json,answered_by)) FROM engine_needs_me", "SELECT group_concat(json_array(id,version,state)) FROM engine_sign_in", "SELECT group_concat(json_array(id,version,auth_state,facts_json)) FROM engine_provider", "SELECT group_concat(json_array(id,version,state,payload_json,receipt_json)) FROM engine_outbox", "SELECT CAST(COUNT(*) AS TEXT) FROM engine_event", "SELECT CAST(COUNT(*) AS TEXT) FROM engine_command_receipt", "SELECT group_concat(json_array(id,version,state,in_flight,attempt)) FROM engine_message" }.Select(sql => Core.Text(sql)));
        public void Dispose() => F.Dispose();
    }
}
