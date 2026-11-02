using Assembly.Infrastructure.Engine.Relays;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class ForeignTitlePartsTests
{
    private static CallContext Call(CancellationToken token) => new(default, "bulk", token, Peer: new OwnerPeer());
    private static RelaySourceCapture Ordinary() => RelayDigests.Capture("A", "B", "M", "session", new("from", "to", "body", "prompt"), "label",
        new(1, "queued", 0, false, 0, "local", null, 0, "B"));
    private static ForeignTitleRequest Request(ForeignTitleSourceCapture capture, string target) =>
        new(ForeignTitleOperation.ForeignAncestry, target, capture.Manifest.SourceMachine, "root", null,
            new(capture.Manifest.SourceMachine, "root", 1, ForeignTitleDigests.Structure(target, capture.Manifest.SourceMachine, "root", capture.Nodes)),
            1, capture.Manifest.NodeFacts, capture.Manifest.Titles);

    [Fact]
    public async Task Ordinary_and_title_families_share_exactly_one_source_and_one_target_slot()
    {
        using var f = new EngineFixture(); f.SeedSession("root");
        var capture = new EngineReader(f.Db, f.Machine).ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, "root", "target"), default);
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        using var op = parts.Enter(Call(closed.Token));
        var old = Ordinary(); var descriptor = RelayDigests.Describe(old); var ordinaryRequest = new RelaySendRequest(descriptor, 0);
        var ordinaryProbe = new RelaySendBeginReply("parts_required", null, new(descriptor.IdentitySha256, "root", null, "retry", null, false));
        var ordinarySource = op.PublishSource(old);
        Assert.Equal("relay_busy", Assert.Throws<CallFailed>(() => op.PublishForeignSource(capture)).Message);
        op.CloseSource(ordinarySource.Handle);
        var titleSource = op.PublishForeignSource(capture);
        Assert.Throws<CallFailed>(() => op.PublishSource(old)); Assert.True(op.CloseForeignSource(titleSource.Handle));
        Assert.False(op.CloseForeignSource(titleSource.Handle));
        var target = op.PublishTarget(ordinaryRequest, ordinaryProbe).Transfer!;
        var request = Request(capture, "target"); var probe = new ForeignTitleReply(ForeignTitleReplyKind.PartsRequired, null, null, null, null);
        Assert.Throws<CallFailed>(() => op.PublishForeignTarget(request, probe)); op.DropTarget(target);
        var titleTarget = op.PublishForeignTarget(request, probe).Handle!;
        Assert.Throws<CallFailed>(() => op.PublishTarget(ordinaryRequest, ordinaryProbe));
        Assert.True(op.DropForeignTarget(titleTarget)); Assert.False(op.DropForeignTarget(titleTarget));
    }

    [Fact]
    public async Task Admitted_prepared_titles_survive_close_and_host_cleanup()
    {
        using var source = new EngineFixture(); using var target = new EngineFixture(); source.SeedSession("root");
        var capture = new EngineReader(source.Db, source.Machine).ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, "root", target.Machine), default);
        var request = Request(capture, target.Machine); var commands = new EngineCommands(target.Machine, target.Limits);
        var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource(); ForeignTitlePayload prepared;
        using (var op = parts.Enter(Call(closed.Token)))
        {
            var handle = op.PublishForeignTarget(request, new(ForeignTitleReplyKind.PartsRequired, null, null, null, null)).Handle!;
            await op.AppendForeignTitle(new(handle, ForeignTitlePartKind.NodeFacts, 0, capture.NodeFacts));
            await op.AppendForeignTitle(new(handle, ForeignTitlePartKind.Titles, 0, capture.Titles));
            prepared = await op.AdmitForeignTitle(new(request, handle), commands, target.Machine);
        }
        closed.Cancel(); await parts.DisposeAsync();
        Assert.True(target.Db.Writer.Write("test.admitted", s => commands.ApplyForeignAncestryBatch(s, prepared.Batch!)).Applied);
        Assert.Equal("Test", target.Text("SELECT title FROM engine_session WHERE id='root'"));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("kind")]
    [InlineData("hash")]
    [InlineData("rebind")]
    [InlineData("extra")]
    public async Task Bad_part_or_changed_stage_identity_cannot_admit_or_mutate(string damage)
    {
        using var source = new EngineFixture(); using var target = new EngineFixture(); source.SeedSession("root");
        var capture = new EngineReader(source.Db, source.Machine).ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, "root", target.Machine), default);
        var request = Request(capture, target.Machine); var commands = new EngineCommands(target.Machine, target.Limits);
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        using var op = parts.Enter(Call(closed.Token));
        var handle = op.PublishForeignTarget(request, new(ForeignTitleReplyKind.PartsRequired, null, null, null, null)).Handle!;
        if (damage is "gap" or "kind" or "extra")
        {
            var args = new ForeignTitleAppendArgs(handle, damage == "kind" ? (ForeignTitlePartKind)99 : ForeignTitlePartKind.Titles,
                damage == "gap" ? 1 : 0, damage == "extra" ? new byte[capture.Titles.Length + 1] : [1]);
            await Assert.ThrowsAsync<CallFailed>(() => op.AppendForeignTitle(args));
        }
        else
        {
            await op.AppendForeignTitle(new(handle, ForeignTitlePartKind.NodeFacts, 0, capture.NodeFacts));
            var bytes = capture.Titles.ToArray(); if (damage == "hash") bytes[^1] ^= 1;
            await op.AppendForeignTitle(new(handle, ForeignTitlePartKind.Titles, 0, bytes));
            if (damage == "rebind") request = request with { NodeFacts = request.NodeFacts with { Sha256 = new string('f', 64) } };
            await Assert.ThrowsAnyAsync<Exception>(() => op.AdmitForeignTitle(new(request, handle), commands, target.Machine));
        }
        Assert.Equal(0, target.Count("engine_session")); Assert.Equal(0, target.Count("engine_command_receipt"));
    }
}
