namespace Assembly.Application.Common.Machines;

/// <summary>One machine as <c>machines/list</c> answers it.</summary>
public sealed record MachineItem(string? Name, string Kind, bool Reachable, string? Reason);

/// <summary>Whether each machine answered last time.</summary>
public sealed class MachineStatus
{
    public MachineStatus(IEnumerable<string> remotes)
    {
    }

    public IReadOnlyList<MachineItem> Items => throw new NotImplementedException();

    public void Reachable(string machine)
    {
    }

    public void Unreachable(string machine, string reason)
    {
    }
}
