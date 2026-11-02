using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class CrossMachineStartTests
{
    [Theory]
    [InlineData("epoch", "stale_epoch")]
    [InlineData("repo", "stale_caller")]
    [InlineData("child", "child_limit")]
    public async Task Final_source_writer_checks_current_caller_repository_and_children_after_network_preparation(string change, string expected)
    {
        await using var f = await StartHosts.Open(children: 1); var h = f.H;
        using var pause = new PreparedPause(); h.Remote.WriteCommitted = pause.Committed;
        var starting = StartMcp(f);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (change == "epoch")
                h.Mac.Fixture.Db.Writer.Write("test.epoch", scope => scope.Execute("UPDATE engine_session SET epoch=1 WHERE id=$id", new SqlArg("$id", f.Parent)));
            else if (change == "repo")
                h.Mac.Fixture.Db.Writer.Write("test.repo", scope => scope.Execute("UPDATE session_info SET repo_url='changed repository' WHERE session_id=$id", new SqlArg("$id", f.Parent)));
            else
            {
                var prepared = EngineCommands.PrepareSessionTitle(new StartSession("codex", "source-model", Path.Combine(f.Root, "mac"), "Current child", f.Parent, null, null));
                Assert.True(((IEngineRuntime)h.Mac.Runtime).Write(scope => h.Mac.Runtime.Commands.ApplyPreparedSessionTitle(scope,
                    new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Session, f.Parent), Guid.NewGuid().ToString(), 0, 0, 1000), prepared)).Applied);
            }
        }
        finally { pause.Release.Set(); }
        var error = await Assert.ThrowsAsync<McpRefused>(() => starting);
        Assert.Equal(expected, error.Refusal.Code);
        Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Rename_during_preparation_keeps_foreign_snapshot_and_captures_current_title_only_for_initial_M()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        using var pause = new PreparedPause(); h.Remote.WriteCommitted = pause.Committed;
        var starting = StartMcp(f);
        var renamed = "Renamed\0AFTER_RENAME";
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Mac.Fixture.Db.Writer.Write("test.rename", scope => scope.Execute("UPDATE engine_session SET title=CAST($title AS TEXT),version=version+1 WHERE id=$id",
                new SqlArg("$title", Encoding.UTF8.GetBytes(renamed)), new SqlArg("$id", f.Parent)));
        }
        finally { pause.Release.Set(); }
        var answer = await starting;
        var foreignTitle = h.Remote.Fixture.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", row => Encoding.UTF8.GetString(row.Blob(0)), default, new SqlArg("$id", f.Parent));
        Assert.Equal("Parent", foreignTitle);
        var message = h.Mac.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB),CAST(sender_label AS BLOB) FROM engine_message WHERE id=$id",
            row => (Body: Encoding.UTF8.GetString(row.Blob(0)), Label: Encoding.UTF8.GetString(row.Blob(1))), default, new SqlArg("$id", answer.MessageId));
        Assert.StartsWith($"Session \"{renamed}\"", message.Body); Assert.Equal($"{renamed} ({f.Parent})", message.Label);
    }

    [Fact]
    public async Task Real_title_race_inside_initial_M_writer_uses_existing_helper_guard_and_rolls_back_source_batch()
    {
        await using var f = await StartHosts.Open(title: new string('L', 35000)); var h = f.H;
        h.Mac.Fixture.Db.Writer.Write("test.race", scope => scope.Execute($"""
            CREATE TRIGGER rename_before_helper AFTER INSERT ON engine_command_receipt
            WHEN NEW.command_id LIKE 'foreign-parent/v1/%'
            BEGIN UPDATE engine_session SET title='changed after preparation' WHERE id='{f.Parent}'; END;
            """));
        var error = await Assert.ThrowsAsync<McpRefused>(() => StartMcp(f));
        Assert.Equal("stale_caller", error.Refusal.Code);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE state='foreign'"));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'foreign-parent/v1/%'"));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
    }

    [Fact]
    public async Task Current_census_and_target_start_complete_under_actual_unrelated_mirror_progress()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var mirror = Assert.Single(h.Connections.CaptureMirrorOwners()).Mirror;
        var proof = await mirror.CaptureLiveBindingAsync(default);
        using var pause = new PreparedPause(); h.Remote.WriteCommitted = pause.Committed;
        var starting = StartMcp(f);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Remote.Commit(5, "unrelated progress"); await h.Reached(h.Remote.Through);
            Assert.Same(proof, await mirror.CaptureLiveBindingAsync(default));
        }
        finally { pause.Release.Set(); }
        var answer = await starting;
        Assert.Equal("starting", h.Remote.Fixture.State("engine_session", answer.SessionId));
        Assert.Equal("queued", h.Mac.Fixture.State("engine_message", answer.MessageId));
    }

    [Fact]
    public async Task Fresh_Mcp_invocation_uses_new_child_and_message_ids_then_obeys_current_child_cap()
    {
        await using var f = await StartHosts.Open(children: 2); var h = f.H;
        var first = await StartMcp(f); await h.Reached(h.Remote.Through);
        var second = await StartMcp(f); await h.Reached(h.Remote.Through);
        Assert.NotEqual(first.SessionId, second.SessionId); Assert.NotEqual(first.MessageId, second.MessageId);
        var refused = await Assert.ThrowsAsync<McpRefused>(() => StartMcp(f));
        Assert.Equal("child_limit", refused.Refusal.Code);
        Assert.Equal(2, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        Assert.Equal(2, h.Mac.Fixture.Count("engine_message"));
        Assert.All(new[] { first.MessageId, second.MessageId }, id => Assert.Equal("queued", h.Mac.Fixture.State("engine_message", id)));
    }

    [Fact]
    public async Task Managed_start_without_actual_source_repository_refuses_before_target_preparation()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        h.Mac.Fixture.Db.Writer.Write("test.no_repo", scope => scope.Execute("UPDATE session_info SET repo_url='' WHERE session_id=$id", new SqlArg("$id", f.Parent)));
        var refused = await Assert.ThrowsAsync<McpRefused>(() => StartMcp(f));
        Assert.Equal("bad_args", refused.Refusal.Code);
        Assert.Null(h.Remote.Fixture.State("engine_session", f.Parent));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Source_runtime_stop_before_initial_M_leaves_accepted_child_without_inventing_prompt_on_restart()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        h.Remote.WriteCommitted = what =>
        {
            if (what != "engine/machine_command" || h.Remote.Fixture.Number(
                "SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)) != 1) return;
            accepted.TrySetResult(); release.Wait();
        };
        var starting = StartMcp(f);
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
            await h.StopMac().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { release.Set(); }
        Assert.NotNull(await Record.ExceptionAsync(() => starting));
        await h.StartMac();
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE state='foreign'"));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id=$id AND state<>'foreign'", new SqlArg("$id", f.Parent)));
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }

    private static Task<StartSessionAnswer> StartMcp(StartHosts f) => new McpMachineStarts(f.Starts).StartAsync(new(f.Parent, 0),
        new(Prompt: "Task", Title: "Child", Machine: f.H.Remote.Runtime.Machine), default);

    private sealed class PreparedPause : IDisposable
    {
        private int _used;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal void Committed(string what)
        {
            if (what != "engine/foreign_title_commit" || Interlocked.Exchange(ref _used, 1) != 0) return;
            Entered.TrySetResult(); Release.Wait();
        }
        public void Dispose() => Release.Dispose();
    }
}
