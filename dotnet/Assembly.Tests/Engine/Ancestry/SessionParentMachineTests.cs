using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Ancestry;

public sealed class SessionParentMachineTests
{
    private const string Parent = "actual-parent";
    private const string Child = "actual-child";
    private const string ForeignOwner = "actual-foreign-owner";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Checked_start_projects_actual_parent_owner_in_snapshot_and_pushed_payload_without_changing_child_version(bool foreign)
    {
        using var f = new EngineFixture();
        EstablishParent(f, foreign);
        var actor = foreign ? new EngineActor(ActorKind.System, "machine:" + ForeignOwner)
            : new EngineActor(ActorKind.Session, Parent);
        Assert.True(f.Apply(new StartSession("codex", "model", "/fixture", "Actual child", Parent, null, null),
            Child, actor: actor).Applied);
        AssertParity(f, Child, foreign ? ForeignOwner : f.Machine);
        Assert.Equal(1, f.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", Child)));
        if (foreign)
        {
            Assert.Equal("forbidden", f.Apply(new RegisterForeignSession("different-owner", "codex", "Untrusted replacement"),
                Parent, actor: new(ActorKind.System, "machine:different-owner")).Error);
            AssertParity(f, Child, ForeignOwner);
        }
        else
        {
            Assert.True(f.Apply(new SetSessionState("ended", null, null), Parent, version: 1,
                actor: new(ActorKind.System, "adapter")).Applied);
            // Parent state changes do not revoke or invent the recorded FK owner relation.
            AssertParity(f, Child, f.Machine);
        }
        Assert.Equal(1, f.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", Child)));
        Assert.Single(f.Events(), item => item.EntityId == Child);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trusted_import_projects_actual_local_or_foreign_parent_with_existing_started_event(bool foreign)
    {
        using var f = new EngineFixture();
        EstablishParent(f, foreign);
        var effects = f.Count("engine_outbox");
        ImportedSession[] imported = [new(Child, Parent, "codex", "recorded-model", "/fixture", "Imported child", "")];
        Assert.Equal(1, f.Db.Writer.Write("test.actual-import", scope => SessionImport.Import(scope, f.Machine, 1000, imported)));
        Assert.Equal(effects, f.Count("engine_outbox"));
        AssertParity(f, Child, foreign ? ForeignOwner : f.Machine);
        Assert.Equal(1, f.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", Child)));
        Assert.Equal("session.started", Assert.Single(f.Events(), item => item.EntityId == Child).Kind);
    }

    [Fact]
    public void Root_has_null_parent_owner_and_missing_parent_is_not_promoted_or_guessed()
    {
        using var f = new EngineFixture();
        Assert.True(f.Apply(new StartSession("codex", "model", "/fixture", "Root", null, null, null), Parent,
            actor: new(ActorKind.Window, "owner")).Applied);
        AssertParity(f, Parent, null);
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        Assert.Equal("bad_args", f.Apply(new StartSession("codex", "model", "/fixture", "Unknown parent", "missing", null, null),
            Child, actor: new(ActorKind.Session, "missing")).Error);
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_session WHERE id=$id", new SqlArg("$id", Child)));
    }

    private static void EstablishParent(EngineFixture f, bool foreign)
    {
        if (!foreign)
        {
            Assert.True(f.Apply(new StartSession("codex", "model", "/fixture", "Parent", null, null, null), Parent,
                actor: new(ActorKind.Window, "owner")).Applied);
            return;
        }
        var commands = new EngineCommands(f.Machine, f.Limits);
        var prepared = commands.PrepareForeignAncestry(new(ForeignOwner, Parent,
            [new(Parent, ForeignOwner, "codex", "Foreign reference", null)]));
        Assert.True(f.Db.Writer.Write("test.actual-foreign-ancestry", scope => commands.ApplyForeignAncestryBatch(scope, prepared)).Applied);
        Assert.Equal(0, f.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", Parent)));
        Assert.Equal("foreign", f.State("engine_session", Parent));
        Assert.Equal(ForeignOwner, f.Text("SELECT machine FROM engine_session WHERE id=$id", new SqlArg("$id", Parent)));
    }

    private static void AssertParity(EngineFixture f, string id, string? parentMachine)
    {
        var reader = new EngineReader(f.Db, f.Machine);
        var high = reader.HighWatermark(default);
        var entity = reader.Entity("session", id, default)!.Value;
        using var snapshot = reader.Snapshot("sessions", id, default);
        var snapshotItem = Assert.Single(snapshot.ReadNext().Items);
        var pushed = reader.Since("mirror", null, 0, default);
        var pushedItem = Assert.Single(pushed.Rows!, item => item.GetProperty("entityKind").GetString() == "session" &&
            item.GetProperty("id").GetString() == id);
        Assert.Equal(parentMachine, entity.GetProperty("parentMachine").GetString());
        Assert.Equal(f.Machine, entity.GetProperty("machine").GetString());
        Assert.Equal(1, entity.GetProperty("version").GetInt64());
        Assert.True(JsonElement.DeepEquals(entity, snapshotItem));
        Assert.True(JsonElement.DeepEquals(entity, pushedItem));
        Assert.Equal(high, reader.HighWatermark(default));
    }
}
