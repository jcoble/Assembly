using Assembly.Domain.Events;
using Assembly.Application.Common.Acp;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Fixtures;

/// <summary>A fixture transcript, replayed as the events the live host would make from it.</summary>
internal static class FixtureReplay
{
    public static IReadOnlyList<AgentEvent> Events(string name) =>
        TranscriptReplay.Events(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static long Ms(string timestamp) => TranscriptReplay.Ms(timestamp);
}
