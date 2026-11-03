using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Phone;
using Assembly.Tests.Engine;
using Assembly.Tests.Engine.Workflows.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Assembly.Tests.Infrastructure.Phone;

public sealed class PhoneNeedsRoutesTests
{
    [Theory]
    [InlineData("/phone/v1/inbox")]
    [InlineData("/phone/v1/inbox/{id}/answer")]
    public async Task Missing_admission_is_refused_before_body_or_engine_access(string route)
    {
        await using var app = WebApplication.CreateBuilder().Build();
        PhoneNeedsRoutes.Map(app, _ => null, null!, null!, (_, _) => throw new InvalidOperationException("must not reach writer"));
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate => candidate.RoutePattern.RawText == route);
        var context = new DefaultHttpContext();
        using var body = new MemoryStream("not JSON"u8.ToArray());
        using var response = new MemoryStream();
        context.Request.Body = body; context.Response.Body = response;
        await endpoint.RequestDelegate!(context);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(0, body.Position);
        using var error = JsonDocument.Parse(response.ToArray());
        Assert.Equal("Pair this phone again", error.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Inbox_uses_actual_permission_ask_labels_and_engine_version()
    {
        using var f = new EngineFixture();
        f.SeedSession(state: "running");
        var card = new AskPayload("permission", "open", "Allow command?", "Run build", null,
            [new("allow", "Allow once", "allow_once"), new("deny", "Reject", "reject_once")], null, null);
        var prepared = EngineCommands.PrepareNeedCommand(new OpenNeed("s", null, null, "permission", "Run build",
            ["allow", "deny"], "provider-request", "actual-ask", null,
            JsonSerializer.SerializeToElement(card, RowJsonContext.Rows.AskPayload)));
        RowStore.Open(f.Db).Commit("s", new RowConverter().Apply(new AskChanged(1000, "actual-ask", card)), scope =>
        {
            var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "adapter"), "actual-ask", 0, 0, 1000);
            Assert.True(new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(scope, context, prepared).Applied);
        });
        var item = Assert.Single(Read(f));
        Assert.Equal("actual-ask", item.Id);
        Assert.Equal(1, item.Version);
        Assert.Equal(0, item.Epoch);
        Assert.True(item.ProviderBacked);
        Assert.Equal("allow", PhoneNeedsRoutes.AnswerFor(item, "allow").GetProperty("optionId").GetString());
        Assert.Equal("Allow command?", item.Title);
        Assert.Equal("Run build", item.Detail);
        Assert.Collection(item.Options,
            option => Assert.Equal(new PhoneNeedsRoutes.Option("allow", "Allow once", "primary"), option),
            option => Assert.Equal(new PhoneNeedsRoutes.Option("deny", "Reject", "danger"), option));
        using var wire = JsonDocument.Parse(JsonReply.Encode(json => PhoneNeedsRoutes.WriteInbox(json, [item])));
        var output = wire.RootElement.GetProperty("items")[0];
        Assert.Equal("actual-ask", output.GetProperty("id").GetString());
        Assert.Equal("permission", output.GetProperty("kind").GetString());
        Assert.Equal(1000, output.GetProperty("createdAt").GetInt64());
        Assert.Equal("Provider", output.GetProperty("evidence")[0].GetProperty("label").GetString());
        Assert.Equal("Allow once", output.GetProperty("options")[0].GetProperty("label").GetString());
        using var array = JsonDocument.Parse(JsonReply.Encode(json => PhoneNeedsRoutes.WriteItems(json, [item])));
        Assert.Equal(wire.RootElement.GetProperty("items").GetRawText(), array.RootElement.GetRawText());
    }

    [Fact]
    public void Inbox_orders_newest_first_and_excludes_resolved_parent_addressed_and_foreign_needs()
    {
        using var f = new EngineFixture();
        f.SeedSession(); f.SeedSession("parent");
        foreach (var id in new[] { "old", "new", "resolved", "parent-only", "foreign" })
        {
            var result = f.Apply(new OpenNeed("s", null, null, "choice", "Choose", ["yes", "no"], null, null, null),
                id, actor: new(ActorKind.System, "test"));
            Assert.True(result.Applied, result.Error);
        }
        f.Db.Writer.Write("test.phone-inbox-filter", scope =>
        {
            scope.Execute("UPDATE engine_needs_me SET created_ms=2000 WHERE id='new'");
            scope.Execute("UPDATE engine_needs_me SET state='answered' WHERE id='resolved'");
            scope.Execute("UPDATE engine_needs_me SET addressee_session_id='parent' WHERE id='parent-only'");
            return scope.Execute("UPDATE engine_needs_me SET machine='another-machine' WHERE id='foreign'");
        });
        Assert.Equal(new[] { "new", "old" }, Read(f).Select(item => item.Id));
        Assert.Empty(f.Db.Writer.Write("test.phone-inbox-id", scope => PhoneNeedsRoutes.Read(scope, f.Machine, "missing")));
        f.SeedSession("other");
        Assert.True(f.Apply(new OpenNeed("other", null, null, "choice", "Other choice", ["yes", "no"], null, null, null),
            "other-need", actor: new(ActorKind.System, "test")).Applied);
        var selected = f.Db.Writer.Write("test.phone-inbox-session", scope => PhoneNeedsRoutes.Read(scope, f.Machine, sessionId: "s"));
        Assert.Equal(new[] { "new", "old" }, selected.Select(item => item.Id));
        Assert.Equal("other-need", Assert.Single(f.Db.Writer.Write("test.phone-inbox-other-session",
            scope => PhoneNeedsRoutes.Read(scope, f.Machine, sessionId: "other"))).Id);
        Assert.Empty(f.Db.Writer.Write("test.phone-inbox-missing-session",
            scope => PhoneNeedsRoutes.Read(scope, f.Machine, sessionId: "missing")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"optionId\":\"allow\"}")]
    [InlineData("{\"optionId\":\"allow\",\"version\":0}")]
    [InlineData("{\"optionId\":\"allow\",\"version\":-1}")]
    [InlineData("{\"optionId\":\"allow\",\"version\":1.5}")]
    [InlineData("{\"optionId\":\"allow\",\"version\":\"1\"}")]
    [InlineData("{\"optionId\":\" \",\"version\":1}")]
    public void Invalid_mobile_answers_do_not_acquire_a_current_version(string json)
    {
        using var body = JsonDocument.Parse(json);
        Assert.False(PhoneNeedsRoutes.TryAnswer(body.RootElement, out _, out _));
    }

    [Fact]
    public void Mobile_answer_retains_the_exact_submitted_option_and_version()
    {
        using var body = JsonDocument.Parse("""{"optionId":"allow","version":17}""");
        Assert.True(PhoneNeedsRoutes.TryAnswer(body.RootElement, out var option, out var version));
        Assert.Equal("allow", option); Assert.Equal(17, version);
    }

    [Theory]
    [InlineData(0, 0, "version_conflict")]
    [InlineData(1, 1, "stale_epoch")]
    public void Checked_owner_answer_does_not_refresh_a_submitted_fence(long version, long epoch, string error)
    {
        using var f = new EngineFixture();
        f.SeedSession();
        Assert.True(f.Apply(new OpenNeed("s", null, null, "choice", "Choose", ["yes", "no"], null, null, null),
            "need", actor: new(ActorKind.System, "test")).Applied);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "need", version, epoch,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), null, null, default);
        var result = f.Db.Writer.Write("test.phone-owner-fence", scope =>
            new EngineCommands(f.Machine, f.Limits).ApplyPreparedOwnerNeed(scope, prepared, 1000));
        Assert.False(result.Applied); Assert.Equal(error, result.Error);
        Assert.Equal("open", f.State("engine_needs_me", "need"));
    }

    [Fact]
    public void Phone_and_desktop_use_one_owner_so_the_second_answer_is_already_resolved()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        Assert.True(f.Apply(new OpenNeed("s", null, null, "choice", "Choose", ["yes", "no"], null, null, null),
            "need", actor: new(ActorKind.System, "test")).Applied);
        var phone = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "need", 1, 0,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), null, null, default);
        var desktop = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "need", 1, 0,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("no")), null, null, default);
        Assert.True(f.Db.Writer.Write("test.phone-owner", scope =>
            new EngineCommands(f.Machine, f.Limits).ApplyPreparedOwnerNeed(scope, phone, 1000)).Applied);
        var second = f.Db.Writer.Write("test.desktop-owner", scope =>
            new EngineCommands(f.Machine, f.Limits).ApplyPreparedOwnerNeed(scope, desktop, 1000));
        Assert.False(second.Applied); Assert.Equal("already_resolved", second.Error);
        Assert.Equal("owner", f.Text("SELECT answered_by FROM engine_needs_me WHERE id='need'"));
        Assert.Empty(Read(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workflow_owner_addressed_need_uses_offered_scalar_and_keeps_generation_fence(bool changeGeneration)
    {
        using var f = new CommandStepTests.Fixture(["printf allowed"], "ask");
        Assert.True(f.Start(f.Prepare()).Applied);
        var effect = f.Claim();
        Assert.True(f.Complete(effect, await f.Host().ExecuteAsync(effect, default)).Applied);
        var item = Assert.Single(Read(f.Core));
        Assert.False(item.ProviderBacked);
        Assert.Equal(new[] { "allow_once", "deny" }, item.Options.Select(option => option.Id));
        var answer = PhoneNeedsRoutes.AnswerFor(item, "allow_once");
        Assert.Equal(JsonValueKind.String, answer.ValueKind);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Core.Machine, item.Id, item.Version, item.Epoch,
            new ResolveNeed("answered", answer), null, null, default);
        if (changeGeneration)
            f.Core.Db.Writer.Write("test.phone-workflow-generation", scope => scope.Execute("UPDATE engine_step SET generation=generation+1"));
        var result = f.Core.Db.Writer.Write("test.phone-workflow-answer", scope => f.Commands.ApplyPreparedOwnerNeed(scope, prepared, 1000));
        Assert.Equal(!changeGeneration, result.Applied);
        if (changeGeneration) Assert.Equal("stale_attempt", result.Error);
        Assert.Equal(changeGeneration ? "open" : "answered", f.Core.State("engine_needs_me", item.Id));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    private static IReadOnlyList<PhoneNeedsRoutes.Item> Read(EngineFixture fixture) =>
        fixture.Db.Writer.Write("test.phone-inbox-read", scope => PhoneNeedsRoutes.Read(scope, fixture.Machine));
}
