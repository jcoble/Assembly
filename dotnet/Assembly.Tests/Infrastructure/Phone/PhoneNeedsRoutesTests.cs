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

    private static IReadOnlyList<PhoneNeedsRoutes.Item> Read(EngineFixture fixture) =>
        fixture.Db.Writer.Write("test.phone-inbox-read", scope => PhoneNeedsRoutes.Read(scope, fixture.Machine));
}
