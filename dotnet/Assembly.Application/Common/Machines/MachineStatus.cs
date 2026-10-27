namespace Assembly.Application.Common.Machines;

/// <summary>
/// One machine as <c>machines/list</c> answers it: <paramref name="Name"/> is null for this machine,
/// <paramref name="Kind"/> is <c>local</c> or <c>remote</c>, and <paramref name="Reason"/> says why it's unreachable.
/// </summary>
public sealed record MachineItem(string? Name, string Kind, bool Reachable, string? Reason);

/// <summary>
/// Whether each machine answered last time. This machine always has; a remote starts "not checked yet", and its
/// <c>RemoteMachine</c> records each answer and each failure. Writers swap the snapshot under the lock; readers take
/// it with none.
/// </summary>
public sealed class MachineStatus(IEnumerable<string> remotes)
{
    private readonly Lock _gate = new();
    private volatile MachineItem[] _items =
        [new(null, "local", true, null), .. remotes.Select(name => new MachineItem(name, "remote", false, "not checked yet"))];

    public IReadOnlyList<MachineItem> Items => _items;

    public void Reachable(string machine) => Set(machine, true, null);

    public void Unreachable(string machine, string reason) => Set(machine, false, reason);

    // A name it doesn't hold is ignored, and an unchanged item isn't swapped.
    private void Set(string machine, bool reachable, string? reason)
    {
        lock (_gate)
        {
            var items = _items;
            var index = Array.FindIndex(items, item => item.Name == machine);
            if (index < 0 || (items[index].Reachable == reachable && items[index].Reason == reason))
                return;
            var next = (MachineItem[])items.Clone();
            next[index] = items[index] with { Reachable = reachable, Reason = reason };
            _items = next;
        }
    }
}
