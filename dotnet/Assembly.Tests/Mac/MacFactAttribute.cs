namespace Assembly.Tests.Mac;

/// <summary>A test of macOS-only code (ImageIO, App Nap): skipped elsewhere, such as a lane on the workbox.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only";
    }
}
