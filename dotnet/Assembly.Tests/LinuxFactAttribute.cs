namespace Assembly.Tests;

/// <summary>A test of Linux-only code (reading /proc): skipped elsewhere, such as on the Mac.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only";
    }
}
