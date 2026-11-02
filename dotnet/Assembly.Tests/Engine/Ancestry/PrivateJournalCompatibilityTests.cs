using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.Ancestry;

public sealed class PrivateJournalCompatibilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Preexisting_prefix_guid_receipt_replays_exactly_without_transition(bool accepted)
    {
        using var f = new EngineFixture();
        var id = Guid.Parse("a55e17a1-0000-4000-8000-000000000001");
        var context = new CommandContext(id, "fixture-target", new(ActorKind.Window, "owner"), "old", 0, 0, 99);
        var json = accepted ? "{\"applied\":true,\"late\":false,\"error\":null,\"version\":4,\"seq\":7,\"epoch\":0}"
            : "{\"applied\":false,\"late\":false,\"error\":\"legacy_refusal\",\"version\":0,\"seq\":0,\"epoch\":0}";
        f.Db.Writer.Write("test.old-receipt", scope => scope.Execute(
            "INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES($id,$hash,$result)",
            new("$id", id.ToString("N")), new("$hash", "c2493a6094aca51bb5454140be2c9fdcb88f6b21b57b90e38c4d90cd910c9355"), new("$result", json)));
        f.Reopen();
        var result = f.Db.Writer.Write("test.replay", scope => EngineJournal.Execute(scope, context, "{}",
            () => throw new InvalidOperationException("old receipt must replay before machine validation")));
        Assert.Equal(accepted, result.Applied);
        Assert.Equal(accepted ? null : "legacy_refusal", result.Error);
        Assert.Equal(json, f.Text("SELECT result_json FROM engine_command_receipt"));
    }

    [Fact]
    public void New_prefix_guid_remains_an_ordinary_valid_command()
    {
        using var f = new EngineFixture();
        var context = new CommandContext(Guid.Parse("a55e17a1-0000-4000-8000-000000000002"), f.Machine,
            new(ActorKind.Window, "owner"), "new", 0, 0, 0);
        var result = f.Db.Writer.Write("test.new-prefix", scope => EngineJournal.Execute(scope, context, "{}",
            () => new(true, false, null, 0, 0, 0)));
        Assert.True(result.Applied);
        Assert.Equal(context.CommandId.ToString("N"), f.Text("SELECT command_id FROM engine_command_receipt"));
    }
}
