using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("foreign")]
    [InlineData("none")]
    public async Task Need_addressee_machine_matches_actual_FK_owner_in_push_snapshot_and_entity(string addressee)
    {
        string? parent = addressee == "none" ? null : "recorded-parent";
        string? expected = addressee switch { "local" => _f.Machine, "foreign" => "recorded-foreign-engine", _ => null };
        if (addressee == "foreign")
        {
            var registered = _f.Apply(new RegisterForeignSession(expected!, "codex", "Recorded foreign parent"), parent!,
                actor: new(ActorKind.System, "machine:" + expected));
            Assert.True(registered.Applied, registered.Error);
        }
        _f.Db.Writer.Write("test.import-family", scope =>
        {
            if (addressee == "local")
                Assert.Equal(1, SessionImport.Import(scope, _f.Machine, 1000,
                    [new(parent!, null, "codex", null, "/recorded/parent", "Recorded parent", "")]));
            return SessionImport.Import(scope, _f.Machine, 1000,
                [new("recorded-child", parent, "codex", null, "/recorded/child", "Recorded child", "")]);
        });
        await Published();
        using var socket = await Open();
        await MirrorSubscribe(socket);
        var initial = await Read(socket);
        Assert.Equal("engine_snapshot", initial.GetProperty("t").GetString());
        Assert.DoesNotContain(initial.GetProperty("items").EnumerateArray(), row => row.GetProperty("entityKind").GetString() == "needs_me");

        var opened = _f.Apply(new OpenNeed("recorded-child", null, null, "question", "Recorded question", [], null, null, null),
            "recorded-need", actor: new(ActorKind.System, "adapter"));
        Assert.True(opened.Applied, opened.Error);
        await Published();
        var pushed = await Read(socket);
        Assert.Equal("engine_events", pushed.GetProperty("t").GetString());
        var row = Assert.Single(pushed.GetProperty("rows").EnumerateArray(), item => item.GetProperty("entityKind").GetString() == "needs_me");
        Assert.Equal("recorded-need", row.GetProperty("id").GetString());
        Assert.Equal(parent, row.GetProperty("addresseeSessionId").GetString());
        Assert.Equal(expected, row.GetProperty("addresseeMachine").GetString());
        Assert.Equal(_f.Machine, row.GetProperty("machine").GetString());
        Assert.Equal(1, row.GetProperty("version").GetInt64());
        Assert.Equal(opened.Version, row.GetProperty("version").GetInt64());
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(row, global::Assembly.Engine.EngineJson.Default.JsonElement).Length, 1, 32 * 1024);
        if (addressee == "foreign") Assert.NotEqual(row.GetProperty("machine").GetString(), row.GetProperty("addresseeMachine").GetString());
        if (addressee == "none") Assert.Equal(JsonValueKind.Null, row.GetProperty("addresseeMachine").ValueKind);

        if (parent is not null)
        {
            var refused = _f.Apply(new RegisterForeignSession("different-engine", "codex", "Conflicting owner"), parent,
                actor: new(ActorKind.System, "machine:different-engine"));
            Assert.False(refused.Applied);
            Assert.Equal("forbidden", refused.Error);
        }
        var entity = _runtime.Reader.Entity("needs_me", "recorded-need", default)!.Value;
        using var snapshot = _runtime.Reader.Snapshot("needs_me", null, default);
        var saved = Assert.Single(snapshot.ReadNext().Items);
        foreach (var item in new[] { entity, saved })
        {
            Assert.Equal(row.GetProperty("addresseeSessionId").GetRawText(), item.GetProperty("addresseeSessionId").GetRawText());
            Assert.Equal(row.GetProperty("addresseeMachine").GetRawText(), item.GetProperty("addresseeMachine").GetRawText());
            Assert.Equal(opened.Version, item.GetProperty("version").GetInt64());
        }
        Assert.Equal(1, _f.Number("SELECT version FROM engine_needs_me WHERE id='recorded-need'"));
    }
}
