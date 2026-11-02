using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Ancestry;

public sealed class ForeignTitleSourceTests
{
    [Fact]
    public void Owning_snapshot_keeps_full_frozen_bytes_through_rename_and_checks_only_ownership()
    {
        using var f = new EngineFixture(); f.SeedSession("root"); f.SeedSession("parent", parentId: "root");
        var title = "\0" + new string('界', 1500000) + "suffix\0";
        f.Db.Writer.Write("test.title", s => s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id='root'", new SqlArg("$title", Encoding.UTF8.GetBytes(title))));
        var reader = new EngineReader(f.Db, f.Machine);
        var capture = reader.ForeignTitleSource(new(ForeignTitleSourceMode.OwnedAncestry, "parent", "target"), default);
        Assert.Equal(new[] { "root", "parent" }, capture.Nodes.Select(n => n.SessionId));
        Assert.Equal(title, capture.Nodes[0].Title); Assert.True(capture.Titles.Length > 4 * 1024 * 1024);
        var original = reader.CheckForeignTitleSource(capture, default);
        f.Db.Writer.Write("test.rename", s => s.Execute("UPDATE engine_session SET title='renamed',version=version+1 WHERE id='root'"));
        Assert.Equal(original, reader.CheckForeignTitleSource(capture, default));
        Assert.Equal(title, capture.Nodes[0].Title);
        f.Db.Writer.Write("test.fence", s => s.Execute("UPDATE engine_session SET epoch=epoch+1 WHERE id='root'"));
        Assert.Equal("source_changed", Assert.Throws<ForeignAncestryRefused>(() => reader.CheckForeignTitleSource(capture, default)).Result.Error);
    }

    [Fact]
    public void Foreign_preview_unknown_chain_and_null_title_never_become_owning_source()
    {
        using var f = new EngineFixture();
        Assert.True(f.Apply(new RegisterForeignSession("other", "codex", "preview"), "foreign",
            actor: new(Assembly.Engine.Contracts.ActorKind.System, "machine:other")).Applied);
        var reader = new EngineReader(f.Db, f.Machine);
        Assert.Throws<ForeignAncestryRefused>(() => reader.ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, "foreign", "target"), default));
        f.SeedSession("child", parentId: "foreign");
        Assert.Throws<ForeignAncestryRefused>(() => reader.ForeignTitleSource(new(ForeignTitleSourceMode.OwnedAncestry, "child", "target"), default));
    }

    [Fact]
    public void Empty_title_is_exact_and_invalid_utf8_is_refused()
    {
        using var f = new EngineFixture(); f.SeedSession("root");
        f.Db.Writer.Write("test.empty", s => s.Execute("UPDATE engine_session SET title=''"));
        var reader = new EngineReader(f.Db, f.Machine);
        var captured = reader.ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, "root", "target"), default);
        Assert.Empty(captured.Titles); Assert.Equal("", Assert.Single(captured.Nodes).Title);
        f.Db.Writer.Write("test.invalid", s => s.Execute("UPDATE engine_session SET title=CAST(X'FF' AS TEXT)"));
        Assert.Throws<ForeignAncestryRefused>(() => reader.ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, "root", "target"), default));
    }
}
