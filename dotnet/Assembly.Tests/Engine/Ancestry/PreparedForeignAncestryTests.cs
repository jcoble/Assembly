using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Ancestry;

public sealed class PreparedForeignAncestryTests
{
    [Fact]
    public void Preparation_binds_actual_receiving_instance_and_original_digest_before_writer()
    {
        using var first = new EngineFixture(); using var second = new EngineFixture();
        var nodes = new ForeignAncestor[] { new("root", "source", "codex", "R\0界", null), new("parent", "source", "codex", "P", "root") };
        var args = new ForeignAncestryArgs("source", "parent", nodes);
        var a = new EngineCommands(first.Machine, first.Limits); var b = new EngineCommands(second.Machine, second.Limits);
        var preparedA = a.PrepareForeignAncestry(args); var preparedB = b.PrepareForeignAncestry(args);
        Assert.Equal(first.Machine, preparedA.TargetMachine); Assert.Equal(second.Machine, preparedB.TargetMachine);
        Assert.Equal(OriginalDigest(first.Machine, args), preparedA.ChainDigest);
        Assert.Equal(OriginalDigest(second.Machine, args), preparedB.ChainDigest);
        Assert.NotEqual(preparedA.ChainDigest, preparedB.ChainDigest);
        nodes[0] = nodes[0] with { Title = "mutated after preparation" };
        Assert.True(first.Db.Writer.Write("test.prepared", s => a.ApplyForeignAncestryBatch(s, preparedA)).Applied);
        Assert.Equal("R\0界", Encoding.UTF8.GetString(first.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id='root'", r => r.Blob(0), default)!));
        var cross = Assert.Throws<ForeignAncestryRefused>(() => { second.Db.Writer.Write("test.cross", s => b.ApplyForeignAncestryBatch(s, preparedA)); });
        Assert.Equal("wrong_machine", cross.Result.Error); Assert.Equal(0, second.Count("engine_session"));
        Assert.Equal(0, second.Count("engine_command_receipt"));
    }

    [Fact]
    public void Scope_identity_is_checked_before_any_reference_or_marker_write()
    {
        using var f = new EngineFixture(); var commands = new EngineCommands(f.Machine, f.Limits);
        var prepared = commands.PrepareForeignAncestry(new("source", "root", [new("root", "source", "codex", "", null)]));
        f.Db.Writer.Write("test.scope", s => s.Execute("UPDATE engine_meta SET machine_id='other'"));
        var refusal = Assert.Throws<ForeignAncestryRefused>(() => { f.Db.Writer.Write("test.wrong_scope", s => commands.ApplyForeignAncestryBatch(s, prepared)); });
        Assert.Equal("wrong_machine", refusal.Result.Error); Assert.Equal(0, f.Count("engine_session"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
    }

    private static string OriginalDigest(string target, ForeignAncestryArgs args)
    {
        using var bytes = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in new string?[] { "foreign-parent-batch/v1", target, args.FromMachine, args.ParentSessionId }
            .Concat(args.RootToParent.SelectMany(n => new[] { n.SessionId, n.Machine, n.Provider, n.Title, n.ParentId })))
        {
            var utf8 = value is null ? null : new UTF8Encoding(false, true).GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, utf8?.Length ?? -1);
            bytes.Write(length); if (utf8 is not null) bytes.Write(utf8);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
