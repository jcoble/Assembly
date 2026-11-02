using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Legacy_command_result_ancestry_mapping_carries_large_title_and_returns_actual_marker_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "b11-title-legacy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var h = new TwoHosts(windowCalls: true, forwardCommands: true, machineLauncher: new RelayTestLauncher(), checkouts: root);
            await h.Start(); await h.StartMirror(); var mirror = Assert.Single(h.Connections.CaptureMirrorOwners()).Mirror;
            var proof = await ReadyForPinned(mirror); var id = Guid.NewGuid().ToString();
            var title = "\0" + new string('界', 1500000) + "end\0";
            var args = new ForeignAncestryArgs(h.Mac.Runtime.Machine, id, [new(id, h.Mac.Runtime.Machine, "codex", title, null)]);
            var applied = await h.Mac.Forwarder!.ForeignAncestryAsync(h.ConnectedMachine, proof.OwnerBinding, args, default, proof);
            Assert.True(applied.Applied);
            var repeated = await h.Mac.Forwarder.ForeignAncestryAsync(h.Remote.Runtime.Machine, args, default);
            Assert.Equal(applied, repeated);
            var raw = h.Remote.Fixture.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$key",
                new SqlArg("$key", Assembly.Engine.Persistence.RelayReceiptKeys.ForeignParent(h.Remote.Runtime.Machine, id)));
            Assert.Equal(raw, System.Text.Json.JsonSerializer.Serialize(repeated, Assembly.Engine.EngineJson.Default.CommandResult));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Actual_owner_lease_reads_full_frozen_titles_through_progress_and_source_rename()
    {
        var root = Path.Combine(Path.GetTempPath(), "b11-title-forward-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var h = new TwoHosts(windowCalls: true, forwardCommands: true, machineLauncher: new RelayTestLauncher(), checkouts: root);
            await h.Start(); var id = Guid.NewGuid().ToString(); h.Remote.Fixture.SeedSession(id);
            var title = "\0" + new string('界', 1500000) + "😀suffix\0";
            h.Remote.Fixture.Db.Writer.Write("test.full_title", s => s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id=$id",
                new SqlArg("$title", Encoding.UTF8.GetBytes(title)), new SqlArg("$id", id)));
            await h.StartMirror(); var mirror = Assert.Single(h.Connections.CaptureMirrorOwners()).Mirror; var proof = await ReadyForPinned(mirror);
            await using var lease = await h.ConnectedMachine.AcquireRelayBulkAsync(proof.OwnerBinding, default, proof);
            var forward = h.Mac.Forwarder!;
            var snapshot = await forward.ReadForeignTitlesAsync(lease, new(ForeignTitleSourceMode.NodeOnly, id, h.Mac.Runtime.Machine), default);
            try
            {
                Assert.Equal(title, Assert.Single(snapshot.Nodes).Title);
                h.Remote.Fixture.Db.Writer.Write("test.rename", s => s.Execute("UPDATE engine_session SET title='renamed' WHERE id=$id", new SqlArg("$id", id)));
                h.Remote.Commit(1, "unrelated progress"); await h.Reached(h.Remote.Through);
                Assert.Same(proof, await mirror.CaptureLiveBindingAsync(default));
                var current = await forward.CheckForeignTitlesAsync(lease, snapshot.Opened, default);
                Assert.Equal(h.Remote.Runtime.Machine, current.SourceMachine);
                Assert.Equal(title, Assert.Single(snapshot.Nodes).Title);
            }
            finally { await forward.CloseForeignTitlesAsync(lease, snapshot.Opened.Handle, default); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Actual_bulk_ancestry_forward_applies_full_titles_then_returns_only_existing_structure()
    {
        var root = Path.Combine(Path.GetTempPath(), "b11-title-target-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var h = new TwoHosts(windowCalls: true, forwardCommands: true, machineLauncher: new RelayTestLauncher(), checkouts: root);
            await h.Start(); await h.StartMirror(); var mirror = Assert.Single(h.Connections.CaptureMirrorOwners()).Mirror; var proof = await ReadyForPinned(mirror);
            await using var lease = await h.ConnectedMachine.AcquireRelayBulkAsync(proof.OwnerBinding, default, proof);
            var id = Guid.NewGuid().ToString(); var title = "\0" + new string('界', 1500000) + "suffix\0";
            var args = new ForeignAncestryArgs(h.Mac.Runtime.Machine, id, [new(id, h.Mac.Runtime.Machine, "codex", title, null)]);
            var first = await h.Mac.Forwarder!.ForeignAncestryTitlesAsync(lease, args, default);
            Assert.Equal(ForeignTitleReplyKind.Applied, first.Kind); Assert.True(first.Result!.Applied);
            var replay = await h.Mac.Forwarder.ForeignAncestryTitlesAsync(lease,
                args with { RootToParent = [args.RootToParent[0] with { Title = "later source title", Provider = "changed" }] }, default);
            Assert.Equal(ForeignTitleReplyKind.ExistingStructure, replay.Kind); Assert.Null(replay.Handle);
            Assert.Equal(Encoding.UTF8.GetBytes(title), h.Remote.Fixture.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", r => r.Blob(0), default, new SqlArg("$id", id)));
            Assert.Equal(1, h.Remote.Fixture.Count("engine_command_receipt"));
        }
        finally { Directory.Delete(root, true); }
    }
}
