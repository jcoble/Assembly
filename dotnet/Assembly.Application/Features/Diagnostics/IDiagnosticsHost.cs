namespace Assembly.Application.Features.Diagnostics;

/// <summary>The operating system's side of the diagnostics calls: process memory, App Nap and the soak files.</summary>
public interface IDiagnosticsHost
{
    MemoryReading Memory();

    void BeginActivity();

    void EndActivity();

    // One line appended to soak-results/<name>; the name is already a plain file name.
    void AppendSoak(string name, string line);
}
