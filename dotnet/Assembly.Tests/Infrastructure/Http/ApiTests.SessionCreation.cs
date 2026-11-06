using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class ApiTests
{
    private async Task<string> CreateSessionAsync() =>
        (await ResultAsync("sessions", "create", CreateArgs("claude"))).GetProperty("sessionId").GetString()!;
    private string CreateArgs(string agent, string? machine = null) => JsonSerializer.Serialize(
        new SessionsFeature.CreateArgs(agent, Guid.NewGuid().ToString("N"), Machine: machine,
            ExpectedSource: new(_engine.Machine, 0, null)), SessionsJson.Default.CreateArgs);
}
