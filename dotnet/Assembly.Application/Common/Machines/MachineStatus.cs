using System.Text.Json;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Common.Machines;

/// <summary>
/// One machine as the <c>machines</c> topic sends it, keyed by its name (<c>local</c> for this machine):
/// <paramref name="Name"/> is null for this machine, <paramref name="Kind"/> is <c>local</c> or <c>remote</c>,
/// <paramref name="Reason"/> says why it's unreachable, and <paramref name="Checked"/> is false for a remote until its
/// first probe answers or fails (until then it's neither up nor down).
/// </summary>
public sealed record MachineItem(string? Name, string Kind, bool Reachable, string? Reason, bool Checked);

/// <summary>
/// Whether each machine answered last time. This machine always has; a remote starts "not checked yet", and its
/// <c>RemoteMachine</c> records each answer and each failure. Writers swap the snapshot and publish the changed item
/// under the lock (status, then Topics); readers take the snapshot with none.
/// </summary>
public sealed class MachineStatus(ITopics topics, IEnumerable<string> remotes)
{
    private readonly Lock _gate = new();
    private volatile MachineItem[] _items =
        [new(null, "local", true, null, true), .. remotes.Select(name => new MachineItem(name, "remote", false, "not checked yet", false))];

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
            if (index < 0 || (items[index].Checked && items[index].Reachable == reachable && items[index].Reason == reason))
                return;
            var next = (MachineItem[])items.Clone();
            next[index] = items[index] with { Reachable = reachable, Checked = true, Reason = reason };
            _items = next;
            topics.Publish(MachinesFeature.Topic, machine, JsonSerializer.Serialize(next[index], SessionsJson.Default.MachineItem));
        }
    }
}
