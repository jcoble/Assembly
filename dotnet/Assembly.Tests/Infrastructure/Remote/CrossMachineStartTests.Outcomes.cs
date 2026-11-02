using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Messages;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class CrossMachineStartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Known_source_M_survives_close_failure_or_owner_disconnect_with_original_result_and_bytes(bool disconnect)
    {
        await using var f = await StartHosts.Open(foreignRootTitle: "Actual ancestor"); var h = f.H;
        var mirror = Assert.Single(h.Connections.CaptureMirrorOwners()).Mirror;
        var proof = (await mirror.CaptureLiveBindingAsync(default))!;
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var invalidation = proof.OwnerBinding.Invalidated.Register(() => revoked.TrySetResult());
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? handler = null; var closeCalls = 0;
        async Task Observe()
        {
            Interlocked.Increment(ref closeCalls); closing.TrySetResult();
            await release.Task;
            if (!disconnect) throw new CallFailed("injected source-close failure");
        }
        h.Remote.MachineReading = (_, _) => handler = Observe();
        var starting = StartMcp(f);
        StoredStart before;
        try
        {
            await closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            before = Stored(f); // The initial M writer has returned before source-close cleanup begins.
            if (disconnect)
            {
                h.Remote.Endpoint.DropAll();
                await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { release.TrySetResult(); }
        var answer = await starting.WaitAsync(TimeSpan.FromSeconds(10));
        if (handler is not null) await Record.ExceptionAsync(() => handler);
        Assert.Equal(before.Id, answer.MessageId); Assert.Equal(before.Child, answer.SessionId);
        Assert.Equal(before, Stored(f)); AssertOneChildAndMessage(f);
        Assert.EndsWith("Task", before.Body); Assert.Equal($"Parent ({f.Parent})", before.Label);
        Assert.Equal(1, closeCalls);
        h.Remote.MachineReading = null;
        await AssertBulkOwnerUsable(f);
    }

    [Fact]
    public async Task Source_close_wire_failure_preserves_actual_initial_M_refusal_without_creating_message()
    {
        await using var f = await StartHosts.Open(title: new string('L', 35000), foreignRootTitle: "Actual ancestor"); var h = f.H;
        h.Mac.Fixture.Db.Writer.Write("test.race", scope => scope.Execute($"""
            CREATE TRIGGER rename_before_helper AFTER INSERT ON engine_command_receipt
            WHEN NEW.command_id LIKE 'foreign-parent/v1/%'
            BEGIN UPDATE engine_session SET title='changed after preparation' WHERE id='{f.Parent}'; END;
            """));
        var closeCalls = 0;
        h.Remote.MachineReading = (_, _) =>
        {
            Interlocked.Increment(ref closeCalls);
            return Task.FromException(new CallFailed("injected source-close failure"));
        };
        var refused = await Assert.ThrowsAsync<McpRefused>(() => StartMcp(f));
        Assert.Equal("stale_caller", refused.Refusal.Code); Assert.Equal(1, closeCalls);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE machine=$machine AND parent_id=$id", new SqlArg("$machine", h.Remote.Runtime.Machine), new SqlArg("$id", f.Parent)));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        h.Remote.MachineReading = null;
        await AssertBulkOwnerUsable(f);
    }

    private static async Task AssertBulkOwnerUsable(StartHosts f)
    {
        var owner = Assert.Single(f.H.Connections.CaptureMirrorOwners());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaughtUp() => ready.TrySetResult();
        owner.Mirror.CaughtUp += CaughtUp;
        try
        {
            if (await owner.Mirror.CaptureLiveBindingAsync(default) is null) await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { owner.Mirror.CaughtUp -= CaughtUp; }
        var proof = (await owner.Mirror.CaptureLiveBindingAsync(default))!;
        await using var lease = await owner.Machine.AcquireRelayBulkAsync(proof.OwnerBinding, default, proof);
        var forward = f.H.Mac.Forwarder!;
        var source = await forward.ReadForeignTitlesAsync(lease,
            new(Assembly.Engine.Sessions.ForeignTitleSourceMode.NodeOnly, f.ForeignRoot!, f.H.Mac.Runtime.Machine), default);
        Assert.Equal(f.ForeignRoot, Assert.Single(source.Nodes).SessionId);
        await forward.CloseForeignTitlesAsync(lease, source.Opened.Handle, default);
    }

    [Fact]
    public async Task Source_writer_response_uncertainty_preserves_actual_committed_M_and_receipt_across_restart()
    {
        await using var f = await StartHosts.Open(foreignRootTitle: "Actual ancestor"); var h = f.H;
        var fault = new IOException("injected post-commit notification failure");
        var fired = 0;
        var sub = h.Mac.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            if (h.Mac.Fixture.Count("engine_message") == 1 && Interlocked.Exchange(ref fired, 1) == 0) throw fault;
        });
        try { var answer = await StartMcp(f); Assert.Equal(Stored(f).Id, answer.MessageId); }
        finally { h.Mac.Topics.Unsubscribe(sub); }
        var before = Stored(f);
        AssertOneChildAndMessage(f);
        await h.StopMac(); await h.StartMac();
        Assert.Equal(before, Stored(f)); AssertOneChildAndMessage(f);
        Assert.StartsWith($"Session \"Parent\" ({f.Parent}) started you.", before.Body);
    }

    [Fact]
    public async Task Actual_committed_M_replays_before_changed_caller_title_and_epoch_after_writer_uncertainty()
    {
        var title = new string('T', 35000) + "\0ORIGINAL";
        await using var f = await StartHosts.Open(title: title); var h = f.H;
        h.Mac.Fixture.Db.Writer.Write("test.caller_after_M", scope => scope.Execute($"""
            CREATE TRIGGER change_caller_after_M AFTER INSERT ON engine_command_receipt
            WHEN json_extract(NEW.result_json,'$.applied')=1 AND EXISTS(SELECT 1 FROM engine_message)
            BEGIN UPDATE engine_session SET epoch=1,title='Changed after M' WHERE id='{f.Parent}'; END;
            """));
        var fault = new IOException("injected response uncertainty after M commit"); var fired = 0;
        var sub = h.Mac.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            if (h.Mac.Fixture.Count("engine_message") == 1 && Interlocked.Exchange(ref fired, 1) == 0) throw fault;
        });
        Assembly.Engine.Mcp.Tools.StartSessionAnswer answer;
        try { answer = await StartMcp(f); }
        finally { h.Mac.Topics.Unsubscribe(sub); }
        var stored = Stored(f);
        Assert.Equal(stored.Id, answer.MessageId); Assert.Equal(stored.Child, answer.SessionId);
        Assert.StartsWith($"Session \"{title}\" ({f.Parent}) started you.", stored.Body);
        Assert.Equal($"{title} ({f.Parent})", stored.Label);
        Assert.Equal(1, h.Mac.Fixture.Number("SELECT epoch FROM engine_session WHERE id=$id", new SqlArg("$id", f.Parent)));
        Assert.Equal("Changed after M", h.Mac.Fixture.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", f.Parent)));
        var refused = await Assert.ThrowsAsync<McpRefused>(() => StartMcp(f));
        Assert.Equal("stale_epoch", refused.Refusal.Code);
        Assert.Equal(stored, Stored(f)); AssertOneChildAndMessage(f);
    }

    [Fact]
    public async Task Retained_M_after_reopen_uses_original_bytes_and_context_and_rejects_conflicts()
    {
        var title = new string('R', 35000) + "\0original";
        await using var f = await StartHosts.Open(title: title); var h = f.H;
        await StartMcp(f);
        var before = Stored(f);
        // Test-held invocation inputs survive runtime reopen; this is no production crash reconstruction.
        var context = new CommandContext(Guid.ParseExact(before.Receipt, "N"), h.Mac.Runtime.Machine,
            new(ActorKind.Session, f.Parent), before.Id, 0, 0, 1);
        var prepared = PreparedHelperPrompt.Prepare(new(f.Parent, before.Child, before.Body, "prompt"), title, 0);
        var retained = new MachineStarts.RetainedInitialPrompt(context, prepared);
        await h.StopMac(); await h.StartMac();
        h.Mac.Fixture.Db.Writer.Write("test.changed_caller", scope => scope.Execute(
            "UPDATE engine_session SET epoch=2,title='renamed',holder='remote',state='ended' WHERE id=$id", new SqlArg("$id", f.Parent)));
        var runtime = (IEngineRuntime)h.Mac.Runtime;
        var replay = runtime.Write(scope => retained.ReplayExisting(scope, h.Mac.Runtime.Commands));
        Assert.NotNull(replay); Assert.True(replay.Applied);
        Assert.Equal(before.Result, System.Text.Json.JsonSerializer.Serialize(replay, EngineJson.Default.CommandResult));
        var changedBody = new MachineStarts.RetainedInitialPrompt(context,
            PreparedHelperPrompt.Prepare(new(f.Parent, before.Child, before.Body + "changed", "prompt"), title, 0));
        var changedContext = new MachineStarts.RetainedInitialPrompt(context with { Epoch = 1 }, prepared);
        foreach (var changed in new[] { changedBody, changedContext })
        {
            var conflict = runtime.Write(scope => changed.ReplayExisting(scope, h.Mac.Runtime.Commands));
            Assert.NotNull(conflict); Assert.False(conflict.Applied); Assert.Equal("command_id_conflict", conflict.Error);
        }
        Assert.Equal(before, Stored(f)); AssertOneChildAndMessage(f);
    }

    [Fact]
    public async Task Retained_M_missing_receipt_is_read_only_even_when_new_admission_has_capacity()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var answer = await StartMcp(f); var before = Stored(f);
        var context = new CommandContext(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Session, f.Parent),
            Guid.NewGuid().ToString(), 0, 0, 1);
        var retained = new MachineStarts.RetainedInitialPrompt(context,
            PreparedHelperPrompt.Prepare(new(f.Parent, answer.SessionId, "Never admitted", "prompt"), "Parent", 0));
        ((IEngineRuntime)h.Mac.Runtime).Write(scope =>
        {
            var changes = scope.QueryFirst("SELECT total_changes()", row => row.Long(0));
            Assert.Null(retained.ReplayExisting(scope, h.Mac.Runtime.Commands));
            Assert.Equal(changes, scope.QueryFirst("SELECT total_changes()", row => row.Long(0)));
            return 0; // Framework audit after this callback is separate from the replay branch.
        });
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", context.CommandId.ToString("N"))));
        Assert.Equal(before, Stored(f)); AssertOneChildAndMessage(f);
    }

    [Fact]
    public async Task Source_writer_failure_without_M_receipt_remains_unknown_and_creates_no_prompt()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var receiptCount = h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE length(command_id)=32");
        h.Mac.Fixture.Db.Writer.Write("test.fail_initial_M", scope => scope.Execute("""
            CREATE TRIGGER fail_initial_M BEFORE INSERT ON engine_message
            BEGIN SELECT RAISE(ABORT,'injected before initial M receipt'); END;
            """));
        var error = await Record.ExceptionAsync(() => StartMcp(f));
        Assert.NotNull(error); Assert.Contains("injected before initial M receipt", error.Message);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(receiptCount, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE length(command_id)=32"));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id", new SqlArg("$id", f.Parent)));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
    }

    [Fact]
    public async Task Retained_M_refused_receipt_returns_its_actual_result_without_new_admission()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var child = await StartMcp(f); var before = Stored(f);
        var retained = new MachineStarts.RetainedInitialPrompt(
            new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Session, f.Parent), Guid.NewGuid().ToString(), 0, 0, 1),
            PreparedHelperPrompt.Prepare(new(f.Parent, child.SessionId, "Refused intent", "bad-mode"), "Parent", 0));
        var runtime = (IEngineRuntime)h.Mac.Runtime;
        var refused = runtime.Write(scope => retained.Apply(scope, h.Mac.Runtime.Commands));
        Assert.False(refused.Applied); Assert.Equal("bad_args", refused.Error);
        var originalReceipt = h.Mac.Fixture.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$id",
            new SqlArg("$id", retained.Context.CommandId.ToString("N")));
        h.Mac.Fixture.Db.Writer.Write("test.changed_epoch", scope => scope.Execute(
            "UPDATE engine_session SET epoch=1,title='renamed' WHERE id=$id", new SqlArg("$id", f.Parent)));
        runtime.Write(scope =>
        {
            var changes = scope.QueryFirst("SELECT total_changes()", row => row.Long(0));
            Assert.Equal(refused, retained.ReplayExisting(scope, h.Mac.Runtime.Commands));
            Assert.Equal(changes, scope.QueryFirst("SELECT total_changes()", row => row.Long(0)));
            return 0;
        });
        Assert.Equal(originalReceipt, h.Mac.Fixture.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$id",
            new SqlArg("$id", retained.Context.CommandId.ToString("N"))));
        Assert.Equal(before, Stored(f)); AssertOneChildAndMessage(f);
    }

    [Theory]
    [InlineData("state='ended'")]
    [InlineData("state='handing_off'")]
    [InlineData("holder='remote'")]
    [InlineData("epoch=1")]
    [InlineData("reserved_epoch=1")]
    [InlineData("cancel_requested=1")]
    public async Task Accepted_Start_current_owning_child_guard_prevents_new_M(string change)
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        h.Remote.WriteCommitted = what =>
        {
            if (what == "engine/machine_command") h.Remote.Fixture.Db.Writer.Write("test.child_changed", scope => scope.Execute(
                $"UPDATE engine_session SET {change} WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        };
        var error = await Assert.ThrowsAsync<CallFailed>(() => StartMcp(f));
        Assert.Equal("unknown", error.Reason);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id", new SqlArg("$id", f.Parent)));
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }

    [Fact]
    public async Task Accepted_Start_title_and_model_progress_preserve_original_intent_and_allow_M()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        h.Remote.WriteCommitted = what =>
        {
            if (what == "engine/machine_command") h.Remote.Fixture.Db.Writer.Write("test.child_renamed", scope => scope.Execute(
                "UPDATE engine_session SET title='renamed',model='progressed',version=version+1 WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        };
        var answer = await StartMcp(f);
        Assert.Equal("renamed", h.Remote.Fixture.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", answer.SessionId)));
        Assert.Equal("progressed", h.Remote.Fixture.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", answer.SessionId)));
        Assert.Equal(answer.MessageId, Stored(f).Id); AssertOneChildAndMessage(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_Start_answer_resolves_same_invocation_at_its_own_child_cap_before_initial_M(bool disconnect)
    {
        await using var f = await StartHosts.Open(children: 1); var h = f.H;
        var starts = 0;
        h.Remote.WriteCommitted = what =>
        {
            if (what != "engine/machine_command") return;
            Interlocked.Increment(ref starts);
            h.Mac.Fixture.Db.Writer.Write("test.rename_after_start", scope => scope.Execute(
                "UPDATE engine_session SET title='Actual caller after ACK' WHERE id=$id", new SqlArg("$id", f.Parent)));
            if (disconnect) h.Remote.Endpoint.DropAll();
            else throw new IOException("injected lost Start answer after actual commit");
        };
        var answer = await StartMcp(f).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, starts); Assert.Equal(answer.SessionId, Stored(f).Child);
        Assert.Equal($"Actual caller after ACK ({f.Parent})", Stored(f).Label);
        Assert.StartsWith($"Session \"Actual caller after ACK\" ({f.Parent}) started you.", Stored(f).Body);
        Assert.EndsWith("Task", Stored(f).Body); AssertOneChildAndMessage(f);
        Assert.Equal("Child", h.Remote.Fixture.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", answer.SessionId)));
    }

    [Theory]
    [InlineData("epoch", "stale_epoch")]
    [InlineData("repo", "stale_caller")]
    [InlineData("cap", "child_limit")]
    public async Task Safe_same_socket_missing_reruns_current_source_policy_before_any_new_Start(string change, string expected)
    {
        await using var f = await StartHosts.Open(children: 1); var h = f.H;
        var prepares = 0;
        h.Remote.MachinePrepared = (_, _) =>
        {
            Interlocked.Increment(ref prepares);
            if (change == "cap")
            {
                var other = EngineCommands.PrepareSessionTitle(new Assembly.Engine.Sessions.StartSession("codex", null,
                    Path.Combine(f.Root, "mac"), "Other child", f.Parent, null, null));
                Assert.True(((IEngineRuntime)h.Mac.Runtime).Write(scope => h.Mac.Runtime.Commands.ApplyPreparedSessionTitle(scope,
                    new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Window, "owner"), Guid.NewGuid().ToString(), 0, 0, 2000), other)).Applied);
            }
            else h.Mac.Fixture.Db.Writer.Write("test.policy_changed_before_missing", scope => scope.Execute(change == "epoch"
                ? "UPDATE engine_session SET epoch=1 WHERE id=$id"
                : "UPDATE session_info SET repo_url='changed repository' WHERE session_id=$id", new SqlArg("$id", f.Parent)));
            throw new IOException("injected prepared turn failed before Start writer admission");
        };
        var error = await Assert.ThrowsAsync<McpRefused>(() => StartMcp(f));
        Assert.Equal(expected, error.Refusal.Code); Assert.Equal(1, prepares);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id", new SqlArg("$id", f.Parent)));
    }

    [Fact]
    public async Task Safe_same_socket_missing_retries_the_same_Start_after_fresh_policy_check()
    {
        await using var f = await StartHosts.Open(children: 1); var h = f.H;
        var prepares = 0;
        h.Remote.MachinePrepared = (_, _) => Interlocked.Increment(ref prepares) == 1
            ? Task.FromException(new IOException("injected first prepared turn failure")) : Task.CompletedTask;
        var answer = await StartMcp(f);
        Assert.Equal(2, prepares); Assert.Equal(answer.SessionId, Stored(f).Child); AssertOneChildAndMessage(f);
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start' AND session_id=$id", new SqlArg("$id", answer.SessionId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_refused_Start_answer_preserves_actual_refusal_before_changed_source_gates(bool disconnect)
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var starts = 0;
        h.Remote.WriteCommitted = what =>
        {
            if (what != "engine/machine_command") return;
            Interlocked.Increment(ref starts);
            h.Mac.Fixture.Db.Writer.Write("test.caller_changed_after_refusal", scope => scope.Execute(
                "UPDATE engine_session SET epoch=1 WHERE id=$id", new SqlArg("$id", f.Parent)));
            if (disconnect) h.Remote.Endpoint.DropAll(); else throw new IOException("injected refused Start answer loss");
        };
        var error = await Assert.ThrowsAsync<McpRefused>(() => new McpMachineStarts(f.Starts).StartAsync(new(f.Parent, 0),
            new(Prompt: "Task", Title: "", Machine: h.Remote.Runtime.Machine), default));
        Assert.Equal("bad_args", error.Refusal.Code); Assert.Equal(1, starts);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id", new SqlArg("$id", f.Parent)));
    }

    [Fact]
    public async Task Replacement_missing_while_original_Start_turn_is_paused_stays_unknown_without_resend_or_M()
    {
        await using var f = await StartHosts.Open(children: 1); var h = f.H;
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepares = 0;
        h.Remote.MachinePrepared = (_, _) => { Interlocked.Increment(ref prepares); prepared.TrySetResult(); return release.Task; };
        h.Remote.WriteCommitted = what => { if (what == "engine/machine_command") committed.TrySetResult(); };
        var starting = StartMcp(f);
        try
        {
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Remote.Endpoint.DropAll();
            var error = await Assert.ThrowsAsync<CallFailed>(() => starting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("unknown", error.Reason); Assert.Equal(1, prepares);
            Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
            Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id", new SqlArg("$id", f.Parent)));
        }
        finally { release.TrySetResult(); }
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Reached(h.Remote.Through);
        Assert.Equal(1, prepares); Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id", new SqlArg("$id", f.Parent)));
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }

    [Fact]
    public async Task Target_state_change_after_first_ACK_is_rechecked_before_new_initial_M()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var acknowledgements = 0;
        h.Remote.WriteCommitted = what =>
        {
            if (what == "engine/start_ack" && Interlocked.Increment(ref acknowledgements) == 1)
                h.Remote.Fixture.Db.Writer.Write("test.child_ended_after_ack", scope => scope.Execute(
                    "UPDATE engine_session SET state='ended' WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        };
        var error = await Assert.ThrowsAsync<CallFailed>(() => StartMcp(f));
        Assert.Equal("unknown", error.Reason); Assert.Equal(2, acknowledgements);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id", new SqlArg("$id", f.Parent)));
    }

    private sealed record StoredStart(string Id, string Child, string Body, string Label,
        string Receipt, string Fingerprint, string Result);

    private static StoredStart Stored(StartHosts f)
    {
        var db = f.H.Mac.Fixture.Db;
        var message = db.QueryFirst("SELECT id,to_session_id,CAST(text AS BLOB),CAST(sender_label AS BLOB) FROM engine_message",
            row => (Id: row.Text(0), Child: row.Text(1), Body: Encoding.UTF8.GetString(row.Blob(2)), Label: Encoding.UTF8.GetString(row.Blob(3))), default);
        var receipt = db.QueryFirst("""
            SELECT command_id,fingerprint,CAST(result_json AS BLOB) FROM engine_command_receipt
            WHERE json_extract(result_json,'$.applied')=1 AND json_extract(result_json,'$.seq')=(
                SELECT seq FROM engine_event WHERE entity_kind='message' AND entity_id=$id AND entity_version=1)
            """, row => (Id: row.Text(0), Fingerprint: row.Text(1), Result: Encoding.UTF8.GetString(row.Blob(2))), default, new SqlArg("$id", message.Id));
        Assert.Matches("^[0-9a-f]{32}$", receipt.Id); Assert.Matches("^[0-9a-f]{64}$", receipt.Fingerprint);
        return new(message.Id, message.Child, message.Body, message.Label, receipt.Id, receipt.Fingerprint, receipt.Result);
    }

    private static void AssertOneChildAndMessage(StartHosts f)
    {
        Assert.Equal(1, f.H.Mac.Fixture.Count("engine_message"));
        Assert.Equal(1, f.H.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }
    [Fact]
    public async Task Cleanup_faults_attempt_all_resources_and_preserve_original_refusal_and_stack()
    {
        var disposed = new List<int>();
        var resources = new[] { new CleanupResource(1, disposed, true), new CleanupResource(2, disposed, false), new CleanupResource(3, disposed, true) };
        var primary = new McpRefused(new("stale_epoch"));
        var error = await Assert.ThrowsAsync<McpRefused>(async () =>
        {
            try { ThrowPrimary(primary); }
            finally { await MachineStarts.CleanupAsync(resources); }
        });
        Assert.Same(primary, error); Assert.Equal("stale_epoch", error.Refusal.Code);
        Assert.Contains(nameof(ThrowPrimary), error.StackTrace);
        Assert.Equal(new[] { 1, 2, 3 }, disposed);
    }

    [Fact]
    public async Task Cleanup_fault_cannot_replace_known_committed_result_or_its_original_ids()
    {
        var disposed = new List<int>();
        var known = new StartSessionAnswer(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        async Task<StartSessionAnswer> Complete()
        {
            try { return known; }
            finally { await MachineStarts.CleanupAsync([new CleanupResource(1, disposed, true), new CleanupResource(2, disposed, false)]); }
        }
        Assert.Same(known, await Complete()); Assert.Equal(new[] { 1, 2 }, disposed);
    }

    private static void ThrowPrimary(McpRefused error) => throw error;

    private sealed class CleanupResource(int id, List<int> disposed, bool fail) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            disposed.Add(id);
            return fail ? ValueTask.FromException(new IOException("injected cleanup failure")) : ValueTask.CompletedTask;
        }
    }
}
