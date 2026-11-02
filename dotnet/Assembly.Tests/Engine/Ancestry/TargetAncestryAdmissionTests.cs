using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Ancestry;

public sealed class TargetAncestryAdmissionTests
{
    private static void ProveParent(EngineFixture f)
    {
        var commands = new EngineCommands(f.Machine, f.Limits);
        var prepared = commands.PrepareForeignAncestry(new("source", "parent",
            [new("root", "source", "codex", "Root", null), new("parent", "source", "codex", "Parent", "root")]));
        Assert.True(f.Db.Writer.Write("test.ancestry", s => commands.ApplyForeignAncestryBatch(s, prepared)).Applied);
    }

    private static CommandResult PeerStart(EngineFixture f, string id) => f.Apply(
        new StartSession("codex", "model", "/test", "Child", "parent", null, null), id,
        actor: new(ActorKind.System, "machine:source"));

    [Theory]
    [InlineData(1, "depth_limit")]
    [InlineData(2, null)]
    [InlineData(3, null)]
    public void Peer_start_uses_the_current_target_depth_cap(int cap, string? error)
    {
        var currentCap = cap;
        using var f = new EngineFixture(limits: new(() => currentCap, () => 5));
        ProveParent(f);
        var result = PeerStart(f, "child");
        Assert.Equal(error, result.Error);
        Assert.Equal(error is null, result.Applied);
        Assert.Equal(error is null ? 1 : 0, f.Number("SELECT COUNT(*) FROM engine_session WHERE id='child'"));
        if (error is not null)
        {
            Assert.Equal(0, f.Count("engine_event"));
            Assert.Equal(0, f.Count("engine_outbox"));
            currentCap = 2;
            Assert.True(PeerStart(f, "fresh-child").Applied);
        }
    }

    [Theory]
    [InlineData("idle", "child_limit")]
    [InlineData("ended", null)]
    public void Peer_start_obeys_the_same_unfinished_direct_child_cap(string state, string? error)
    {
        using var f = new EngineFixture(limits: new(() => 3, () => 1));
        ProveParent(f);
        f.SeedSession("older", state, "parent");
        var result = PeerStart(f, "child");
        Assert.Equal(error, result.Error);
        Assert.Equal(error is null, result.Applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Peer_start_requires_private_proof_for_the_entire_foreign_chain(bool corruptMarker)
    {
        using var f = new EngineFixture(limits: new(() => 3, () => 5));
        if (corruptMarker)
        {
            ProveParent(f);
            f.Db.Writer.Write("test.corrupt", s => s.Execute(
                "UPDATE engine_command_receipt SET fingerprint='bad' WHERE command_id=$key",
                new SqlArg("$key", RelayReceiptKeys.ForeignParent(f.Machine, "root"))));
        }
        else
            Assert.True(f.Apply(new RegisterForeignSession("source", "codex", "Parent"), "parent",
                actor: new(ActorKind.System, "machine:source")).Applied);
        Assert.Equal("bad_args", PeerStart(f, "child").Error);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_session WHERE id='child'"));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public async Task Mcp_caller_depth_and_next_child_include_the_whole_proven_foreign_chain()
    {
        var cap = 2;
        var topics = new Topics();
        using var f = new EngineFixture(limits: new(() => cap, () => 5), topics: topics);
        ProveParent(f);
        f.SeedSession("caller", parentId: "parent");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var gate = new McpGate(runtime, f.Limits, f.Time);
        Assert.Equal(2, gate.Read(new("caller", 0), (row, _) => row.Depth, default));
        var refusal = Assert.Throws<McpRefused>(() => gate.Write(new("caller", 0), w => w.Apply("child", 0, 0,
            new StartSession("codex", "model", "/test", "Child", "caller", null, null))));
        Assert.Equal("depth_limit", refusal.Refusal.Code);
        Assert.Equal("3", refusal.Refusal.Values!["depth"]);
        Assert.Equal("2", refusal.Refusal.Values["limit"]);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_session WHERE id='child'"));
        cap = 3;
        Assert.True(gate.Write(new("caller", 0), w => w.Apply("child", 0, 0,
            new StartSession("codex", "model", "/test", "Child", "caller", null, null))).Applied);
        Assert.Equal(3, gate.Read(new("child", 0), (row, _) => row.Depth, default));
    }

    [Theory]
    [InlineData("idle", 0, "local", "bad_args")]
    [InlineData("idle", 1, "local", "stale_epoch")]
    [InlineData("handing_off", 0, "local", "handing_off")]
    [InlineData("ended", 0, "local", "session_gone")]
    [InlineData("lost", 0, "local", "session_gone")]
    [InlineData("idle", 0, "elsewhere", "session_gone")]
    public async Task Unproven_ancestry_never_shortens_depth_and_preserves_prior_caller_gates(
        string state, long epoch, string holder, string error)
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        Assert.True(f.Apply(new RegisterForeignSession("source", "codex", "Parent"), "parent",
            actor: new(ActorKind.System, "machine:source")).Applied);
        f.SeedSession("caller", state, "parent", holder: holder, epoch: epoch);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var gate = new McpGate(runtime, f.Limits, f.Time);
        Assert.Equal(error, Assert.Throws<McpRefused>(() => gate.Read(new("caller", 0),
            (row, _) => row.Depth, default)).Refusal.Code);
        Assert.Equal(error, Assert.Throws<McpRefused>(() => gate.Write(new("caller", 0),
            w => w.Caller.Depth)).Refusal.Code);
    }
}
