using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class ApiTests
{
    private async Task<string> CreateSessionAsync() =>
        (await ResultAsync("sessions", "create", CreateArgs("claude"))).GetProperty("sessionId").GetString()!;
    private string CreateArgs(string agent, string? machine = null) => JsonSerializer.Serialize(new
    {
        agent,
        clientId = Guid.NewGuid().ToString("N"),
        machine,
        expectedSource = new ExpectedSource(_engine.Machine, 0, null)
    });
}
