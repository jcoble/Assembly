using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Ancestry;

public sealed class ForeignAncestryTests
{
    private const string Root = "root";
    private const string Parent = "parent";
    private static ForeignAncestryArgs Chain(string source = "source") => new(source, Parent,
        [new(Root, source, "codex", "Root", null), new(Parent, source, "codex", "Parent", Root)]);

    private static CommandResult Batch(EngineFixture f, ForeignAncestryArgs? args = null) =>
        f.Db.Writer.Write("test.ancestry", s => new EngineCommands(f.Machine, f.Limits)
            .ApplyForeignAncestryBatch(s, args ?? Chain()));

    [Fact]
    public void Batch_is_invisible_version_zero_and_replays_after_reopen()
    {
        using var f = new EngineFixture();
        Assert.True(Batch(f).Applied);
        Assert.Equal(2, f.Count("engine_session"));
        Assert.Equal(0, f.Number("SELECT SUM(version+managed_checkout) FROM engine_session"));
        Assert.Equal(Root, f.Text("SELECT parent_id FROM engine_session WHERE id='parent'"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'foreign-parent/v1/%'"));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_outbox"));
        f.Reopen();
        Assert.True(Batch(f).Applied);
        Assert.Equal(2, f.Count("engine_command_receipt"));
        var repeat = f.Apply(new RegisterForeignSession("source", "codex", "Changed"), Parent,
            actor: new(ActorKind.System, "machine:source"));
        Assert.True(repeat.Applied);
        Assert.Equal(Root, f.Text("SELECT parent_id FROM engine_session WHERE id='parent'"));
        Assert.Equal("Parent", f.Text("SELECT title FROM engine_session WHERE id='parent'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generic_same_context_guid_never_allocates_or_reads_private_marker(bool genericFirst)
    {
        using var f = new EngineFixture();
        var key = RelayReceiptKeys.ForeignParent(f.Machine, Root);
        var context = new CommandContext(RelayReceiptKeys.ContextGuid(key), f.Machine,
            new(ActorKind.System, "machine:source"), Root, 0, 0, 0);
        var canonical = JsonSerializer.Serialize<EngineCommand>(new EstablishForeignParent(null), EngineJson.Default.EngineCommand);
        if (!genericFirst) Assert.True(Batch(f).Applied);
        var generic = f.Db.Writer.Write("test.generic", s => EngineJournal.Execute(s, context, canonical,
            () => new(false, false, "forbidden", 0, 0, 0)));
        Assert.Equal("forbidden", generic.Error);
        Assert.True(Batch(f).Applied);
        Assert.Equal(generic, f.Db.Writer.Write("test.replay", s => EngineJournal.Execute(s, context, canonical,
            () => throw new InvalidOperationException("must replay"))));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", context.CommandId.ToString("N"))));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", key)));
    }

    [Fact]
    public void Generic_establishment_is_forbidden_and_registration_does_not_prove_root()
    {
        using var f = new EngineFixture();
        Assert.True(f.Apply(new RegisterForeignSession("source", "codex", "Root"), Root,
            actor: new(ActorKind.System, "machine:source")).Applied);
        Assert.Equal("forbidden", f.Apply(new EstablishForeignParent(null), Root,
            actor: new(ActorKind.System, "machine:source")).Error);
        Assert.False(f.Db.Writer.Write("test.chain", s => SessionAncestry.Read(s, f.Machine, Root).Known));
        Assert.True(Batch(f).Applied);
        Assert.True(f.Db.Writer.Write("test.chain", s => SessionAncestry.Read(s, f.Machine, Root).Known));
    }

    [Fact]
    public void Marker_body_owner_and_actual_link_are_required()
    {
        using var f = new EngineFixture();
        Assert.True(Batch(f).Applied);
        var key = RelayReceiptKeys.ForeignParent(f.Machine, Parent);
        f.Db.Writer.Write("test.corrupt", s => s.Execute("UPDATE engine_command_receipt SET fingerprint='bad' WHERE command_id=$id", new SqlArg("$id", key)));
        Assert.False(f.Db.Writer.Write("test.chain", s => SessionAncestry.Read(s, f.Machine, Parent).Known));
        Assert.Throws<ForeignAncestryRefused>(() => Batch(f));
        Assert.Equal(2, f.Count("engine_session"));
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("link")]
    [InlineData("receipt")]
    public void Fault_at_each_node_boundary_rolls_back_entire_supplied_writer(string boundary)
    {
        using var f = new EngineFixture();
        var sql = boundary switch
        {
            "reference" => "CREATE TRIGGER fail_reference BEFORE INSERT ON engine_session WHEN NEW.id='parent' BEGIN SELECT RAISE(ABORT,'reference'); END",
            "link" => "CREATE TRIGGER fail_link BEFORE UPDATE OF parent_id ON engine_session WHEN NEW.id='parent' BEGIN SELECT RAISE(ABORT,'link'); END",
            _ => "CREATE TRIGGER fail_receipt BEFORE INSERT ON engine_command_receipt WHEN (SELECT COUNT(*) FROM engine_command_receipt)=1 BEGIN SELECT RAISE(ABORT,'receipt'); END"
        };
        f.Db.Writer.Write("test.fault", s => s.Execute(sql));
        Assert.Throws<SqliteException>(() => Batch(f));
        Assert.Equal(0, f.Count("engine_session"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.Empty(f.Events());
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Whole_chain_is_validated_before_any_write()
    {
        using var f = new EngineFixture();
        var args = Chain() with { RootToParent = [new(Root, "source", "codex", "R", null), new(Parent, "source", "codex", "P", "missing")] };
        Assert.Throws<ForeignAncestryRefused>(() => Batch(f, args));
        Assert.Equal(0, f.Count("engine_session"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Proven_foreign_ancestry_counts_full_depth(int cap, bool allowed)
    {
        using var f = new EngineFixture(limits: new(() => cap, () => 5));
        Assert.True(Batch(f).Applied);
        f.SeedSession("child", parentId: Parent);
        var chain = f.Db.Writer.Write("test.chain", s => SessionAncestry.Read(s, f.Machine, "child"));
        Assert.True(chain.Known);
        Assert.Equal(3, chain.Nodes.Count);
        Assert.Equal(allowed ? null : "depth_limit", f.Db.Writer.Write("test.check", s => MachineStartCheck.Check(s, "child", cap, 5)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generic_success_or_registration_at_marker_guid_is_not_parent_authority(bool registration)
    {
        using var f = new EngineFixture();
        var context = EngineJournal.ForeignParentContext(f.Machine, Chain().RootToParent[0]);
        var result = registration
            ? f.Apply(context, new RegisterForeignSession("source", "codex", "Existing unknown root"))
            : f.Db.Writer.Write("test.generic-success", scope => EngineJournal.Execute(scope, context, "{}",
                () => new(true, false, null, 0, 0, 0)));
        Assert.True(result.Applied);
        Assert.True(Batch(f).Applied);
        Assert.Equal(3, f.Count("engine_command_receipt"));
        Assert.Equal(result, registration
            ? f.Apply(context, new RegisterForeignSession("source", "codex", "Existing unknown root"))
            : f.Db.Writer.Write("test.generic-replay", scope => EngineJournal.Execute(scope, context, "{}",
                () => throw new InvalidOperationException("must replay"))));
    }

    [Fact]
    public void Invalid_last_private_marker_is_rejected_before_first_reference_write()
    {
        using var f = new EngineFixture();
        f.Db.Writer.Write("test.poison", scope =>
        {
            scope.Execute("INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES($key,'bad','{}')",
                new SqlArg("$key", RelayReceiptKeys.ForeignParent(f.Machine, Parent)));
            return scope.Execute("CREATE TRIGGER no_reference BEFORE INSERT ON engine_session BEGIN SELECT RAISE(ABORT,'must prevalidate markers'); END");
        });
        Assert.Throws<ForeignAncestryRefused>(() => Batch(f));
        Assert.Equal(0, f.Count("engine_session"));
        Assert.Equal(1, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Exact_title_bytes_are_preserved_without_structural_rewrite()
    {
        using var f = new EngineFixture();
        var title = "prefix\0" + new string('界', 30000) + "e\u0301";
        var args = Chain() with { RootToParent = [new(Root, "source", "codex", title, null), Chain().RootToParent[1]] };
        Assert.True(Batch(f, args).Applied);
        var bytes = f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id='root'", r => r.Blob(0), default);
        Assert.Equal(new System.Text.UTF8Encoding(false, true).GetBytes(title), bytes);
        Assert.True(Batch(f, args with { RootToParent = [args.RootToParent[0] with { Title = "Later title", Provider = "different" }, args.RootToParent[1]] }).Applied);
        Assert.Equal(bytes, f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id='root'", r => r.Blob(0), default));
    }

    [Fact]
    public void Shared_chain_proof_has_two_reads_for_many_foreign_ancestors()
    {
        using var f = new EngineFixture();
        var nodes = Enumerable.Range(0, 12).Select(i => new ForeignAncestor("n" + i, "source", "codex", "Node", i == 0 ? null : "n" + (i - 1))).ToArray();
        Assert.True(Batch(f, new("source", "n11", nodes)).Applied);
        f.Db.Writer.Write("test.count", scope =>
        {
            var counted = new CountingScope(scope);
            var chain = SessionAncestry.Read(counted, f.Machine, "n11");
            Assert.True(chain.Known);
            Assert.Equal(12, chain.Nodes.Count);
            Assert.Equal(2, counted.Reads);
            return 0;
        });
    }

    [Fact]
    public void Actual_local_root_and_multiple_foreign_owners_share_one_complete_chain()
    {
        using var f = new EngineFixture();
        f.SeedSession("local-root");
        var args = new ForeignAncestryArgs("source", Parent,
            [new("local-root", f.Machine, "codex", "Owning title stays", null),
             new(Root, "third-machine", "codex", "Third", "local-root"),
             new(Parent, "source", "codex", "Parent", Root)]);
        Assert.True(Batch(f, args).Applied);
        Assert.Equal(2, f.Count("engine_command_receipt"));
        Assert.Equal("Test", f.Text("SELECT title FROM engine_session WHERE id='local-root'"));
        var chain = f.Db.Writer.Write("test.chain", scope => SessionAncestry.Read(scope, f.Machine, Parent));
        Assert.True(chain.Known);
        Assert.Equal(new[] { Parent, Root, "local-root" }, chain.Nodes.Select(n => n.Id));
        Assert.Equal("third-machine", f.Text("SELECT machine FROM engine_session WHERE id='root'"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("missing-row")]
    public void Failed_missing_or_orphaned_private_marker_never_certifies_or_rebuilds_history(string damage)
    {
        using var f = new EngineFixture();
        Assert.True(Batch(f).Applied);
        var key = RelayReceiptKeys.ForeignParent(f.Machine, Parent);
        f.Db.Writer.Write("test.damage", scope => scope.Execute(damage switch
        {
            "failed" => "UPDATE engine_command_receipt SET result_json='{\"applied\":false,\"late\":false,\"error\":\"forbidden\",\"version\":0,\"seq\":0,\"epoch\":0}' WHERE command_id=$key",
            "missing" => "DELETE FROM engine_command_receipt WHERE command_id=$key",
            _ => "DELETE FROM engine_session WHERE id='parent' AND $key IS NOT NULL"
        }, new SqlArg("$key", key)));
        Assert.False(f.Db.Writer.Write("test.chain", scope => SessionAncestry.Read(scope, f.Machine, Parent).Known));
        var sessions = f.Count("engine_session");
        var receipts = f.Count("engine_command_receipt");
        Assert.Throws<ForeignAncestryRefused>(() => Batch(f));
        Assert.Equal(sessions, f.Count("engine_session"));
        Assert.Equal(receipts, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Changed_actual_owner_uses_same_slot_and_cannot_recertify_new_owner()
    {
        using var f = new EngineFixture();
        Assert.True(Batch(f).Applied);
        f.Db.Writer.Write("test.owner-damage", scope => scope.Execute("UPDATE engine_session SET machine='other',holder='other'"));
        var error = Assert.Throws<ForeignAncestryRefused>(() => Batch(f, Chain("other")));
        Assert.Equal("command_id_conflict", error.Result.Error);
        Assert.Equal(2, f.Count("engine_command_receipt"));
        Assert.False(f.Db.Writer.Write("test.chain", scope => SessionAncestry.Read(scope, f.Machine, Parent).Known));
    }

    private sealed class CountingScope(IReadScope source) : IReadScope
    {
        internal int Reads { get; private set; }
        public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args)
        { Reads++; return source.Query(sql, map, args); }
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args)
        { Reads++; return source.QueryFirst(sql, map, args); }
    }

}
