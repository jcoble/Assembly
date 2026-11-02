using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Effects;

namespace Assembly.Tests.Engine.Delivery;

public sealed class AuthProducerOrderingTests
{
    [Fact]
    public async Task Failed_terminal_writer_never_claims_a_durable_known_refusal()
    {
        await using var f = new AdapterDeliveryFixture(configured: (provider, account) => provider == "claude" && account == "");
        var message = f.Queue(); var effect = f.Claim(message);
        var execution = f.Effects.ExecuteAsync(effect, default);
        var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            var done = f.Hosts.For(f.Session.SessionId).CurrentTurn.Done;
            f.Db.Writer.Write("test.fail-auth-terminal", scope => scope.Execute("""
                CREATE TRIGGER fail_auth_terminal BEFORE INSERT ON rows
                WHEN NEW.kind='turn' AND json_extract(NEW.payload,'$.state')='failed'
                BEGIN SELECT RAISE(ABORT,'auth terminal writer fault'); END
                """));
            await agent.ReplyErrorAsync(request, -32000, "Sign in");
            var receipt = await execution.WaitAsync(TimeSpan.FromSeconds(8));
            await done.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(EffectDisposition.Unknown, receipt.Disposition);
            Assert.DoesNotContain(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "error" ||
                row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "failed");
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_refusal_keeps_owning_effect_inflight_until_actual_terminal_writer_commits(bool failedOpen)
    {
        await using var f = new AdapterDeliveryFixture(worker: true,
            configured: (provider, account) => provider == "claude" && account == "", gateWrites: true);
        using var live = f.Hub.Subscribe(f.Session.SessionId, 0);
        var message = f.Queue();
        var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            if (!failedOpen)
            {
                await agent.ReplyAsync(request, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
                await agent.OpenAsync(); request = await agent.ReadAsync("session/prompt");
            }
            var effect = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$id",
                r => r.Text(0), default, new SqlArg("$id", message))!;
            f.DatabaseGate!.Arm("rows.commit");
            await agent.ReplyErrorAsync(request, -32000, "Sign in");
            await f.DatabaseGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("inflight", f.State("engine_outbox", effect));
            Assert.Null(f.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.IsNull(0) ? null : r.Text(0), default, new SqlArg("$id", effect)));
            f.DatabaseGate.Release();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            StoredRow terminal;
            do { terminal = await live.Rows.ReadAsync(timeout.Token); }
            while (terminal.Kind != "turn" || terminal.Payload.GetProperty("state").GetString() != "failed");
            var proof = terminal.Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!.AuthFailure!;
            Assert.Equal(failedOpen ? 2 : 1, proof.Version);
            await f.StateAsync("engine_outbox", effect, "done");
            using var scope = f.Db.OpenSnapshot(default);
            Assert.True(EngineCommands.IsAcceptedAuthTerminal(scope, f.Session.SessionId, terminal.Seq, proof));
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0), default));
            Assert.Equal("auth_required", f.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", message)));
            if (!failedOpen) await agent.CloseAsync();
        }
        finally { f.DatabaseGate?.Release(); agent.Crash(); }
    }
}
