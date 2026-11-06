using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Delivery;

public sealed class AuthProducerTests
{
    [Theory]
    [InlineData(AuthOpenFailureStage.Initialize)]
    [InlineData(AuthOpenFailureStage.NewSession)]
    [InlineData(AuthOpenFailureStage.ResumeSession)]
    public async Task Owning_worker_failed_open_retains_exact_terminal_proof(AuthOpenFailureStage stage)
    {
        await using var f = new AdapterDeliveryFixture(worker: true, configured: (provider, account) => provider == "claude" && account == "");
        if (stage == AuthOpenFailureStage.ResumeSession) f.Sessions.SetAcpSession(f.Session.SessionId, "saved-prior-acp");
        using var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
        var message = f.Queue();
        var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            if (stage != AuthOpenFailureStage.Initialize)
            {
                await agent.ReplyAsync(request, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
                request = await agent.ReadAsync(stage == AuthOpenFailureStage.NewSession ? "session/new" : "session/resume");
                if (stage == AuthOpenFailureStage.ResumeSession)
                    Assert.Equal("saved-prior-acp", request.GetProperty("params").GetProperty("sessionId").GetString());
            }
            await agent.ReplyErrorAsync(request, -32000, "Sign in");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            StoredRow terminal;
            while (true)
            {
                terminal = await live.Rows.ReadAsync(timeout.Token);
                if (terminal.Kind == "turn" && terminal.Payload.GetProperty("state").GetString() == "failed") break;
            }
            var proof = Assert.IsType<AuthTerminalProof>(terminal.Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!.AuthFailure);
            Assert.Equal(2, proof.Version); Assert.Equal(stage, proof.OpenFailure);
            Assert.Null(proof.AcpSessionId); Assert.Null(proof.ProviderMessageId);
            Assert.NotEqual(Guid.Empty, proof.ObservationId); Assert.NotEqual(Guid.Empty, proof.BlockCommandId);
            Assert.NotEqual(proof.ObservationId, proof.BlockCommandId);
            Assert.Equal(terminal.RunId, proof.ChatRunId);
            Assert.Equal("C", proof.ClientId);
            var delivery = Assert.IsType<AuthTerminalDelivery>(proof.Delivery);
            Assert.Equal(message, delivery.MessageId); Assert.Equal("C", delivery.ClientId);
            using var snapshot = f.Db.OpenSnapshot(default);
            var user = f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "C")!;
            Assert.Equal(user.Seq, delivery.UserRowSeq);
            Assert.Equal(user.RunId, delivery.UserRunId);
            Assert.Equal(user.RunId, proof.ChatRunId);
            Assert.Equal(1, f.Launcher.Launches);
            Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "error");
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM rows WHERE session_id=$session AND kind='turn' AND json_extract(payload,'$.state')='failed'", r => r.Long(0), default, new SqlArg("$session", f.Session.SessionId)));
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_normal_refusal_waits_for_real_helper_terminal_and_preserves_C6_and_exact_negative_replay(bool failedOpen)
    {
        await using var f = new AdapterDeliveryFixture(worker: true, configured: (provider, account) => provider == "claude" && account == "", gateWrites: true);
        var child = f.Sessions.Helper(f.Session, "claude", "Captured helper");
        using var live = f.Hub.Subscribe(child.SessionId, 0);
        var message = f.Queue(sessionId: child.SessionId);
        var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            if (!failedOpen)
            {
                await agent.ReplyAsync(request, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
                await agent.OpenAsync(); request = await agent.ReadAsync("session/prompt");
            }
            var effectId = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$id", r => r.Text(0), default, new SqlArg("$id", message))!;
            f.Db.Writer.Write("test.persisted-expiry-at-completion", scope => scope.Execute("UPDATE engine_message SET expires_ms=$now WHERE id=$id",
                new SqlArg("$now", f.Time.GetUtcNow().ToUnixTimeMilliseconds()), new("$id", message)));
            f.DatabaseGate!.Arm("rows.commit");
            await agent.ReplyErrorAsync(request, -32000, "Sign in");
            await f.DatabaseGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("inflight", f.State("engine_outbox", effectId));
            f.DatabaseGate.Release();
            await f.StateAsync("engine_outbox", effectId, "done");
            Assert.Equal("failed", f.State("engine_message", message));
            Assert.Equal("expired", f.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", message)));
            var originalReceipt = f.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", effectId))!;
            var receipt = JsonSerializer.Deserialize(originalReceipt, EngineJson.Default.EffectReceipt)!;
            Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
            var data = receipt.Data.Deserialize(EngineJson.Default.MessageEffectData)!;
            Assert.Equal("queued", data.State); Assert.Equal("auth_required", data.Reason); Assert.Null(data.AuthRefusal);
            f.DatabaseGate.Release();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            StoredRow terminal;
            while (true)
            {
                terminal = await live.Rows.ReadAsync(timeout.Token);
                if (terminal.Kind == "turn" && terminal.Payload.GetProperty("state").GetString() == "failed") break;
            }
            if (!failedOpen) await FinishActualParentReplyAndCloseChild(agent);
            await f.Hosts.For(child.SessionId).CurrentTurn.Done.WaitAsync(TimeSpan.FromSeconds(8));
            var proof = Assert.IsType<AuthTerminalProof>(terminal.Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!.AuthFailure);
            Assert.Equal(failedOpen ? 2 : 1, proof.Version);
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_provider", r => r.Long(0), default));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_usage WHERE session_id=$id", r => r.Long(0), default, new SqlArg("$id", child.SessionId)));
            using (var snapshot = f.Db.OpenSnapshot(default))
            {
                var user = f.Rows.UserDeliveryOfClient(snapshot, child.SessionId, "C")!;
                Assert.Null(user.Acknowledgment); Assert.Null(user.AuthBlocker); Assert.Null(user.RetryExecution);
                Assert.False(EngineCommands.IsAcceptedAuthTerminal(snapshot, child.SessionId, terminal.Seq, proof));
            }
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE from_session_id=$id AND text=''", r => r.Long(0), default, new SqlArg("$id", child.SessionId)));
            var journal = f.Db.Query("SELECT result_json FROM engine_command_receipt WHERE command_id IN ($block,$report) ORDER BY command_id", r => r.Text(0), default,
                new SqlArg("$block", proof.BlockCommandId.ToString("N")), new("$report", proof.ObservationId.ToString("N")));
            Assert.Equal(2, journal.Count);
            foreach (var value in journal)
            {
                var result = JsonSerializer.Deserialize(value, EngineJson.Default.CommandResult)!;
                Assert.False(result.Applied); Assert.False(result.Late); Assert.Equal("expired_unbound_terminal", result.Error);
                Assert.Equal(0, result.Version); Assert.Equal(0, result.Seq); Assert.Equal(proof.Epoch, result.Epoch);
            }
            var before = f.Rows.LastSeq(child.SessionId);
            var feed = new EngineFeed(f.Runtime, TextWriter.Null, f.Time); var observed = feed.Capture(child.SessionId)!;
            f.Db.Writer.Write("test.exact-delayed-terminal-replay", scope =>
            {
                if (failedOpen) feed.FailedOpenSignInNeeded(scope, observed, terminal.Seq, proof);
                else feed.SignInNeeded(scope, observed, proof.AcpSessionId!, terminal.Seq, proof);
                return 0;
            });
            Assert.Equal(before, f.Rows.LastSeq(child.SessionId));
            Assert.Equal(originalReceipt, f.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", effectId)));
            await f.RestartAsync();
            Assert.Equal(before, f.Rows.LastSeq(child.SessionId));
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE from_session_id=$id AND text=''", r => r.Long(0), default, new SqlArg("$id", child.SessionId)));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
        }
        finally { f.DatabaseGate?.Release(); agent.Crash(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatibility_auth_terminal_without_owning_delivery_keeps_ordinary_empty_helper_reply(bool failedOpen)
    {
        await using var f = new AdapterDeliveryFixture(configured: (provider, account) => provider == "claude" && account == "");
        var child = f.Sessions.Helper(f.Session, "claude", "Compatibility helper");
        await f.StartAsync(child.SessionId);
        var host = f.Hosts.For(child.SessionId);
        var sending = host.SendAsync("compatibility body", "compatibility-client", null, default);
        var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            if (!failedOpen)
            {
                await agent.ReplyAsync(request, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
                await agent.OpenAsync(); request = await agent.ReadAsync("session/prompt");
            }
            var done = host.CurrentTurn.Done;
            await agent.ReplyErrorAsync(request, -32000, "Sign in");
            Assert.IsType<Assembly.Application.Features.Chat.Sessions.SendResult.AuthRequired>(await sending.WaitAsync(TimeSpan.FromSeconds(8)));
            if (!failedOpen) await agent.CloseAsync();
            await done.WaitAsync(TimeSpan.FromSeconds(8));
            var terminal = Assert.Single(f.Rows.RowsAfter(child.SessionId, 0), row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "failed");
            Assert.Null(terminal.Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!.AuthFailure);
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_provider", r => r.Long(0), default));
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE from_session_id=$id AND text=''", r => r.Long(0), default, new SqlArg("$id", child.SessionId)));
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task Real_terminal_report_writer_fault_rolls_back_proof_blocker_and_C6_together()
    {
        await using var f = new AdapterDeliveryFixture(worker: true, configured: (provider, account) => provider == "claude" && account == "", gateWrites: true);
        var child = f.Sessions.Helper(f.Session, "claude", "Rollback helper");
        f.Queue(sessionId: child.SessionId);
        var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            var done = f.Hosts.For(child.SessionId).CurrentTurn.Done;
            var before = f.Rows.LastSeq(child.SessionId);
            var receipts = f.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default);
            f.Db.Writer.Write("test.fail-real-auth-report", scope => scope.Execute("""
                CREATE TRIGGER fail_real_auth_report BEFORE INSERT ON engine_command_receipt
                WHEN EXISTS(SELECT 1 FROM rows WHERE kind='turn' AND json_type(payload,'$.authFailure')='object'
                  AND replace(json_extract(payload,'$.authFailure.observationId'),'-','')=NEW.command_id)
                BEGIN SELECT RAISE(ABORT,'auth terminal writer fault'); END
                """));
            f.DatabaseGate!.Arm("engine.effect.complete");
            await agent.ReplyErrorAsync(request, -32000, "Sign in");
            await f.DatabaseGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await done.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(before, f.Rows.LastSeq(child.SessionId));
            Assert.Equal(receipts, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_provider", r => r.Long(0), default));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE from_session_id=$id", r => r.Long(0), default, new SqlArg("$id", child.SessionId)));
            using var snapshot = f.Db.OpenSnapshot(default);
            Assert.Null(f.Rows.UserDeliveryOfClient(snapshot, child.SessionId, "C")!.AuthBlocker);
            Assert.DoesNotContain(f.Rows.RowsAfter(child.SessionId, 0), row => row.Kind == "error" || row.Payload.TryGetProperty("authFailure", out _));
        }
        finally { f.DatabaseGate?.Release(); agent.Crash(); }
    }

    [Fact]
    public async Task Actual_positive_insertion_before_auth_failure_keeps_provider_identity_and_acknowledgment()
    {
        await using var f = new AdapterDeliveryFixture(worker: true, configured: (provider, account) => provider == "claude" && account == "");
        using var live = f.Hub.Subscribe(f.Session.SessionId, 0);
        var message = f.Queue(); var agent = await f.Launcher.NextAsync();
        try
        {
            await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
            await agent.InsertAsync(prompt, "actual-positive-provider");
            await f.StateAsync("engine_message", message, "next_turn");
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"error","error":{"code":-32000,"message":"Sign in"}}""");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            StoredRow terminal;
            while (true)
            {
                terminal = await live.Rows.ReadAsync(timeout.Token);
                if (terminal.Kind == "turn" && terminal.Payload.GetProperty("state").GetString() == "failed") break;
            }
            await agent.CloseAsync(); await f.Hosts.For(f.Session.SessionId).CurrentTurn.Done.WaitAsync(TimeSpan.FromSeconds(8));
            var proof = Assert.IsType<AuthTerminalProof>(terminal.Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!.AuthFailure);
            Assert.Equal(1, proof.Version); Assert.Null(proof.OpenFailure);
            Assert.Equal(FakeLink.AcpSessionId, proof.AcpSessionId); Assert.Equal("actual-positive-provider", proof.ProviderMessageId);
            Assert.Equal("failed", f.State("engine_message", message));
            Assert.Equal("inserted_before_sign_in", f.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", message)));
            using var snapshot = f.Db.OpenSnapshot(default);
            var user = f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "C")!;
            Assert.Equal("actual-positive-provider", user.Acknowledgment!.ProviderMessageId);
            Assert.True(EngineCommands.IsAcceptedAuthTerminal(snapshot, f.Session.SessionId, terminal.Seq, proof));
            Assert.Equal(user.Seq, proof.Delivery!.UserRowSeq); Assert.Equal(user.RunId, proof.Delivery.UserRunId);
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0), default));
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task Same_client_join_keeps_one_actual_prompt_and_shared_result()
    {
        await using var f = new AdapterDeliveryFixture(); var host = f.Hosts.For(f.Session.SessionId);
        var first = host.SendAsync("shared body", "joined", "Captured", default, "peer/x");
        var agent = await f.Launcher.NextAsync();
        try
        {
            await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
            var joined = host.SendAsync("shared body", "joined", "Captured", default, "peer/x");
            Assert.False(first.IsCompleted); Assert.False(joined.IsCompleted);
            await agent.InsertAsync(prompt, "one-provider-insertion");
            Assert.Equal(await first.WaitAsync(TimeSpan.FromSeconds(8)), await joined.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
            Assert.Equal(1, f.Launcher.Launches);
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
            await agent.CloseAsync();
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("holder")]
    [InlineData("reservation")]
    public async Task Actual_Connected_refusal_closes_accepted_open_without_prompt_or_auth_terminal(string change)
    {
        await using var f = new AdapterDeliveryFixture();
        f.Sessions.SetTitle(f.Session.SessionId, "Already titled", "user");
        var host = f.Hosts.For(f.Session.SessionId); var sending = host.SendAsync("body", "connected-client", null, default);
        var agent = await f.Launcher.NextAsync();
        try
        {
            var initialize = await agent.ReadAsync("initialize");
            await agent.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[],"_meta":{"steering":{"supported":true}}}""");
            var opening = await agent.ReadAsync("session/new"); var done = host.CurrentTurn.Done;
            f.Db.Writer.Write("test.changed-connected-owner", scope => scope.Execute(change switch
            {
                "epoch" => "UPDATE engine_session SET epoch=epoch+1 WHERE id=$id",
                "holder" => "UPDATE engine_session SET holder='remote' WHERE id=$id",
                _ => "UPDATE engine_session SET reserved_epoch=epoch+1 WHERE id=$id"
            }, new SqlArg("$id", f.Session.SessionId)));
            await agent.ReplyAsync(opening, """{"sessionId":"accepted-before-refusal"}""");
            Assert.Equal("accepted-before-refusal", await agent.CloseAsync());
            var result = Assert.IsType<Assembly.Application.Features.Chat.Sessions.SendResult.Failed>(await sending.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal("provider_fact_refused", result.Reason);
            await done.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Null(f.Db.QueryFirst("SELECT provider_session_id FROM engine_session WHERE id=$id", row => row.IsNull(0) ? null : row.Text(0), default, new SqlArg("$id", f.Session.SessionId)));
            Assert.Equal(0, f.Db.QueryFirst("SELECT can_steer FROM engine_session WHERE id=$id", row => row.Long(0), default, new SqlArg("$id", f.Session.SessionId)));
            Assert.DoesNotContain(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Payload.TryGetProperty("authFailure", out _));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", row => row.Long(0), default));
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task Actual_Connected_identity_writer_fault_rolls_back_capability_identity_and_event_before_prompt()
    {
        await using var f = new AdapterDeliveryFixture();
        f.Sessions.SetTitle(f.Session.SessionId, "Already titled", "user");
        var host = f.Hosts.For(f.Session.SessionId); var sending = host.SendAsync("body", "connected-fault", null, default);
        var agent = await f.Launcher.NextAsync();
        try
        {
            var initialize = await agent.ReadAsync("initialize");
            await agent.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[],"_meta":{"steering":{"supported":true}}}""");
            var opening = await agent.ReadAsync("session/new"); var done = host.CurrentTurn.Done;
            var version = f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId);
            var events = f.Db.QueryFirst("SELECT COUNT(*) FROM engine_event", row => row.Long(0), default);
            f.Db.Writer.Write("test.fail-connected-identity", scope => scope.Execute("""
                CREATE TRIGGER fail_connected_identity BEFORE UPDATE OF provider_session_id ON engine_session
                BEGIN SELECT RAISE(ABORT,'identity writer fault'); END
                """));
            await agent.ReplyAsync(opening, """{"sessionId":"accepted-before-fault"}""");
            Assert.Equal("accepted-before-fault", await agent.CloseAsync());
            Assert.IsType<Assembly.Application.Features.Chat.Sessions.SendResult.Failed>(await sending.WaitAsync(TimeSpan.FromSeconds(8)));
            await done.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Null(f.Db.QueryFirst("SELECT provider_session_id FROM engine_session WHERE id=$id", row => row.IsNull(0) ? null : row.Text(0), default, new SqlArg("$id", f.Session.SessionId)));
            Assert.Equal(0, f.Db.QueryFirst("SELECT can_steer FROM engine_session WHERE id=$id", row => row.Long(0), default, new SqlArg("$id", f.Session.SessionId)));
            Assert.Equal(version, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId));
            Assert.Equal(events, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_event", row => row.Long(0), default));
            Assert.DoesNotContain(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Payload.TryGetProperty("authFailure", out _));
        }
        finally { agent.Crash(); }
    }

    private static async Task FinishActualParentReplyAndCloseChild(FakeLink agent)
    {
        const string parentAcp = "parent-after-helper-reply";
        var childClosed = false; var parentClosed = false; var parentOpened = false; var parentInserted = false;
        while (!childClosed || !parentClosed)
        {
            var request = await agent.NextMessageAsync();
            var method = request.GetProperty("method").GetString();
            switch (method)
            {
                case "session/new":
                    Assert.False(parentOpened); parentOpened = true;
                    await agent.ReplyAsync(request, """{"sessionId":"parent-after-helper-reply"}""");
                    break;
                case "session/prompt":
                    Assert.True(parentOpened); Assert.False(parentInserted); parentInserted = true;
                    Assert.Equal(parentAcp, request.GetProperty("params").GetProperty("sessionId").GetString());
                    await agent.InsertAsync(request, "actual-parent-reply-insertion");
                    await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", parentAcp);
                    break;
                case "session/close":
                    var session = request.GetProperty("params").GetProperty("sessionId").GetString();
                    if (session == FakeLink.AcpSessionId) { Assert.False(childClosed); childClosed = true; }
                    else { Assert.Equal(parentAcp, session); Assert.True(parentInserted); Assert.False(parentClosed); parentClosed = true; }
                    await agent.ReplyAsync(request, "{}");
                    break;
                default: Assert.Fail("Unexpected provider method during actual parent reply: " + method); break;
            }
        }
    }
}
