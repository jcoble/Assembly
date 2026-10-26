using Assembly.Tests.Features.Diagnostics;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests;

/// <summary>A test of macOS-only code (ImageIO, App Nap): skipped elsewhere, such as a lane on the workbox.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only";
    }
}
