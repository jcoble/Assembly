using Assembly.Application.Features.Diagnostics;
using Assembly.Infrastructure.Diagnostics;

namespace Assembly.Tests.Features.Diagnostics;

public sealed class MemorySampleTests
{
    // Each pid's RSS is the pid times ten, so a wrong pid shows.
    private static long? Rss(int pid) => pid * 10L;

    [Fact]
    public void An_app_with_no_WebContent_answers_the_app_and_backend_with_WebContent_null()
    {
        Assert.Equal(new MemoryReading(20, 200, null, null, 10, 100), MemorySample.Reading(10, 20, null, Rss));
    }

    [Fact]
    public void An_app_with_WebContent_answers_all_three()
    {
        Assert.Equal(new MemoryReading(20, 200, 30, 300, 10, 100), MemorySample.Reading(10, 20, 30, Rss));
    }

    [Fact]
    public void With_no_app_only_the_backend_is_measured()
    {
        Assert.Equal(new MemoryReading(null, null, null, null, 10, 100), MemorySample.Reading(10, null, null, Rss));
    }
}
