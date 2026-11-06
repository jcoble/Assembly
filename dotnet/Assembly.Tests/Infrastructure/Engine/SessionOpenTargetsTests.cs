using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;
using Assembly.Engine.Mirror;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Infrastructure.Engine;

public sealed class SessionOpenTargetsTests
{
    [Fact]
    public void Engine_and_historical_are_explicit_and_history_browsing_does_not_start_an_engine_session()
    {
        using var f = new Fixture();
        var history = f.Sessions.Create("codex", "repo");
        var request = new SessionOpenTarget("engine", f.Engine.Machine, history.SessionId, RequestId: 7);
        Assert.Equal("unknown_engine_session", f.Refusal(request));
        var historical = f.Read(request with { Kind = "historical" });
        Assert.True(historical.ReadOnly); Assert.False(historical.HasHistoryRows);
        Assert.Equal(new(f.Engine.Machine, history.SessionId), historical.OriginalKey); Assert.Equal(7UL, historical.RequestId);
        Assert.Equal(0, f.Engine.Count("engine_session"));
        f.Engine.SeedSession(history.SessionId, state: "ended");
        Assert.Equal("nonresumable", f.Refusal(request));
        Assert.True(f.Read(request with { Kind = "historical" }).ReadOnly);
        var current = f.Sessions.Create("codex", "repo"); f.Engine.SeedSession(current.SessionId);
        Assert.False(f.Read(new("engine", f.Engine.Machine, current.SessionId)).ReadOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_history_requires_exact_persisted_card_and_real_readable_child_binding(bool legacyChildPayload)
    {
        using var f = new Fixture();
        var parent = f.Sessions.Create("codex", "repo");
        f.Checkout(parent.SessionId, "/recorded-parent-checkout");
        var child = f.Sessions.Child(parent, "native-child", "Recorded child");
        // SOURCE-DERIVED persisted display variants, not provider/native capture evidence.
        var payloadSession = legacyChildPayload ? child.SessionId : parent.SessionId;
        var target = f.Native(parent, payloadSession);
        Assert.Equal("native_history_unavailable", f.Refusal(target));
        f.History(child.SessionId);
        var before = f.Engine.Count("engine_session");
        var read = f.Read(target);
        Assert.Equal(child.SessionId, read.SessionId); Assert.Equal(parent.SessionId, read.ParentSessionId);
        Assert.Equal(payloadSession, read.PayloadSessionId); Assert.True(read.ReadOnly); Assert.True(read.HasHistoryRows);
        Assert.Null(read.CheckoutRoot); Assert.True(read.TranscriptOnly); // The child's absent root is not its parent's.
        f.Checkout(child.SessionId, "/recorded-child-checkout");
        Assert.Equal("/recorded-child-checkout", f.Read(target).CheckoutRoot);
        Assert.Equal(new(f.Engine.Machine, parent.SessionId), read.OriginalKey);
        Assert.Equal(before, f.Engine.Count("engine_session"));
        Assert.Equal("bad_args", f.Refusal(target with { Parent = new("other-owner", parent.SessionId) }));
        Assert.Equal("native_card_changed", f.Refusal(target with { PayloadSessionId = "unrelated" }));
        f.Card(parent.SessionId, payloadSession);
        Assert.Equal("native_card_changed", f.Refusal(target));
        var latest = target with { Seq = target.Seq + 1 };
        Assert.Equal(child.SessionId, f.Read(latest).SessionId);
        f.Card(parent.SessionId, payloadSession, helper: true);
        Assert.Equal("native_card_changed", f.Refusal(latest with { Seq = latest.Seq + 1 }));
    }

    [Fact]
    public void Missing_native_hydration_never_manufactures_an_empty_child_or_engine_fallback()
    {
        using var f = new Fixture(); var parent = f.Sessions.Create("codex", "repo");
        var target = f.Native(parent, parent.SessionId);
        var before = f.Engine.Number("SELECT COUNT(*) FROM session_info");
        Assert.Equal("native_history_unavailable", f.Refusal(target));
        Assert.Equal(before, f.Engine.Number("SELECT COUNT(*) FROM session_info"));
        Assert.Equal(0, f.Engine.Count("engine_session"));
        Assert.Equal("native_card_changed", f.Refusal(target with { ItemId = "subagent:unrecorded-child" }));
    }

    [Fact]
    public void A_current_saved_route_is_not_proof_that_cached_history_belongs_to_its_replacement_owner()
    {
        using var f = new Fixture();
        var remote = f.Sessions.Add("same-id", "codex", "repo", "workbox-test");
        var source = f.Snapshot("actual-owner");
        var request = new SessionOpenTarget("historical", source.Machine, remote.SessionId);
        // The actual owner bridge is deliberately required; this pure reader never certifies cached remote rows.
        Assert.Equal("owner_validation_required", f.Refusal(request, source));
        Assert.Equal("source_changed", f.Refusal(request, source with { Generation = source.Generation + 1 }));
        Assert.Equal("source_changed", f.Refusal(request, source with { RouteMachine = "other-box" }));
        f.Snapshot("replacement-owner");
        Assert.Equal("source_changed", f.Refusal(request, source));
        Assert.Equal("source_changed", f.Refusal(new("historical", f.Engine.Machine, remote.SessionId)));
    }

    [Fact]
    public void A_local_history_facade_cannot_override_an_actual_foreign_engine_identity()
    {
        using var f = new Fixture(); var session = f.Sessions.Create("codex", "repo");
        var registration = f.Engine.Apply(new RegisterForeignSession("actual-owner", "codex", "Foreign session"),
            session.SessionId, actor: new(ActorKind.System, "machine:actual-owner"));
        Assert.True(registration.Applied, registration.Error);
        Assert.Equal("source_changed", f.Refusal(new("historical", f.Engine.Machine, session.SessionId)));
        Assert.Equal("actual-owner", f.Engine.Text("SELECT machine FROM engine_session WHERE id=$id", new SqlArg("$id", session.SessionId)));
    }

    [Fact]
    public void Historical_checkout_metadata_survives_ended_history_without_filesystem_authorization()
    {
        using var f = new Fixture(); var session = f.Sessions.Create("codex", "repo");
        f.Engine.SeedSession(session.SessionId, state: "ended"); f.History(session.SessionId);
        var request = new SessionOpenTarget("historical", f.Engine.Machine, session.SessionId);
        var absent = f.Read(request);
        Assert.Null(absent.CheckoutRoot); Assert.True(absent.TranscriptOnly);
        Assert.True(absent.ReadOnly); Assert.True(absent.HasHistoryRows);
        // SOURCE-DERIVED recorded metadata only; no folder is created, inspected, or authorized by this read.
        f.Checkout(session.SessionId, "/recorded-but-unavailable-checkout");
        var recorded = f.Read(request);
        Assert.Equal("/recorded-but-unavailable-checkout", recorded.CheckoutRoot); Assert.False(recorded.TranscriptOnly);
        Assert.True(recorded.ReadOnly); Assert.True(recorded.HasHistoryRows);
        f.Checkout(session.SessionId, "");
        Assert.Null(f.Read(request).CheckoutRoot); Assert.True(f.Read(request).TranscriptOnly);
    }

    [Fact]
    public void Cancellation_prevents_target_validation()
    {
        using var f = new Fixture(); using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => f.Targets.Validate(
            new("historical", f.Engine.Machine, "unknown"), f.Local, stop.Token));
    }

    private sealed class Fixture : IDisposable
    {
        public EngineFixture Engine { get; } = new();
        public SessionStore Sessions { get; }
        public RowStore Rows { get; }
        public SessionOpenTargets Targets { get; }
        public ExpectedSource Local => new(Engine.Machine, 0, null);
        public Fixture()
        {
            Rows = RowStore.Open(Engine.Db);
            Sessions = SessionStore.Open(Engine.Db, Engine.Time, "/isolated-checkouts");
            Targets = new(Engine.Db, Sessions);
        }
        public SessionOpenTargetRead Read(SessionOpenTarget target) => Targets.Validate(target, Local, default);
        public string? Refusal(SessionOpenTarget target, ExpectedSource? source = null) =>
            Assert.Throws<CallFailed>(() => Targets.Validate(target, source ?? Local, default)).Reason;
        public SessionOpenTarget Native(SessionInfo parent, string payloadSession)
        {
            Card(parent.SessionId, payloadSession);
            return new("native", Engine.Machine, parent.SessionId, "subagent:native-child",
                Rows.RowsAfter(parent.SessionId, 0).Last().Seq, payloadSession, new(Engine.Machine, parent.SessionId));
        }
        public void Card(string parent, string payloadSession, bool? helper = null)
        {
            var payload = new SubagentPayload(payloadSession, "Source-derived native card", null, "idle", null, null, false, null, Helper: helper);
            var batch = new RowBatch();
            batch.Rows.Add(new(null, "subagent:native-child", RowKind.Subagent,
                JsonSerializer.SerializeToElement(payload, RowJsonContext.Rows.SubagentPayload), 1000));
            Rows.Commit(parent, batch);
        }
        public void History(string child)
        {
            var converter = new RowConverter();
            Rows.Commit(child, converter.Apply(new TurnStarted(1000, "source-derived-history", "Historical turn")));
            Rows.Commit(child, converter.Apply(new TurnEnded(1001, "end_turn", null)));
        }
        public void Checkout(string session, string root) => Engine.Db.Writer.Write("test.recorded-checkout", scope =>
        {
            // SetCheckout refuses empty input; this source-derived legacy empty value tests read classification only.
            if (root.Length == 0) scope.Execute("UPDATE session_info SET checkout_root='' WHERE session_id=$id", new SqlArg("$id", session));
            else Sessions.SetCheckout(scope, session, root, null);
            return 0;
        });
        public ExpectedSource Snapshot(string owner) => Engine.Db.Writer.Write("test.source-snapshot", scope =>
        {
            MirrorApply.Open(scope, owner, "workbox-test", 1000);
            MirrorApply.BeginSnapshot(scope, owner, 1000);
            var source = MirrorApply.EndSnapshot(scope, owner, 0, null, 1000);
            return new ExpectedSource(owner, source.Generation, "workbox-test");
        });
        public void Dispose() => Engine.Dispose();
    }
}
