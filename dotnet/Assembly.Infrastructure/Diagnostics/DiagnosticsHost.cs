using Assembly.Application.Features.Diagnostics;

namespace Assembly.Infrastructure.Diagnostics;

/// <summary>The diagnostics calls' operating system side: <see cref="MemorySample"/>, <see cref="TurnActivity"/> and the soak files.</summary>
internal sealed class DiagnosticsHost(string soakResults) : IDiagnosticsHost
{
    private readonly TurnActivity _activity = new();

    public MemoryReading Memory() => MemorySample.Read();

    public bool ActivityHeld => _activity.Held;

    public void HoldActivity(bool held) => _activity.Hold(held);

    public void AppendSoak(string name, string line)
    {
        Directory.CreateDirectory(soakResults);
        File.AppendAllText(Path.Combine(soakResults, name), line + "\n");
    }
}
