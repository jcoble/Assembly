using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Common.Machines;

/// <summary>
/// One machine as the <c>machines</c> topic sends it, keyed by its name (<c>local</c> for this machine):
/// <paramref name="Name"/> is null for this machine, <paramref name="Kind"/> is <c>local</c> or <c>remote</c>,
/// <paramref name="Reason"/> describes connection warnings, and <paramref name="Checked"/> is false for a remote until its
/// first probe answers or fails (until then it's neither up nor down). <paramref name="Backend"/> is what a remote's
/// last welcome said it runs. <paramref name="Updates"/> is the update check's last answer; the one update's progress or
/// failure is <paramref name="UpdateNote"/>.
/// </summary>
public sealed record MachineItem(string? Name, string Kind, bool Reachable, string? Reason, bool Checked, string? Ssh = null, int? Port = null, string? TokenFile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] RemoteBackend? Backend = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] IReadOnlyList<UpdateItem>? Updates = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Updating = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? UpdateNote = null);

/// <summary>One piece the update check looked at: <paramref name="Kind"/> is desktop, backend, adapter or cli.</summary>
public sealed record UpdateItem(string Kind, string Name, string? Current, string? Wanted, string State)
{
    public const string Behind = "behind", Unknown = "unknown", UpToDate = "current", Newer = "newer";
}

/// <summary>
/// Which backend a remote runs, compared with this desktop's. Information only. <paramref name="Differs"/> is any
/// difference in the backend's build inputs, what a check before a PR requires. <see cref="VersionDiffers"/> is the one
/// number: what makes a remote behind, in development and in production.
/// </summary>
public sealed record RemoteBackend(string? Commit, string? LocalCommit, bool Differs, string? Version, string? LocalVersion)
{
    public bool VersionDiffers => Version != LocalVersion;
}

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

    private MachineStatus? _parent;
    private bool _active = true;
    private readonly Dictionary<string, MachineStatus> _scopes = [];
    public IReadOnlyList<MachineItem> Items => _items;

    // Each RemoteMachine gets a scope that stops forwarding callbacks before it is disposed.
    public MachineStatus Scope(string name)
    {
        lock (_gate)
        {
            Retire(name);
            return _scopes[name] = new(topics, []) { _parent = this };
        }
    }
    public MachineStatus ForProbe(string name)
    {
        lock (_gate) return _scopes.GetValueOrDefault(name) ?? Scope(name);
    }
    // Called inside the profile write, before commit: any old publication has finished before its removal/update.
    public void Retire(string name)
    {
        lock (_gate)
            if (_scopes.TryGetValue(name, out var scope)) scope._active = false;
    }
    public void Stop() { lock ((_parent ?? this)._gate) _active = false; }
    public void Profile(SavedMachine profile)
    {
        lock (_gate)
            _items = [.. _items.Where(item => item.Name != profile.Name), MachineStore.Item(profile)];
    }
    public void Remove(string name)
    {
        lock (_gate)
        {
            _items = [.. _items.Where(item => item.Name != name)];
            _scopes.Remove(name);
        }
    }

    public void Backend(string machine, RemoteBackend backend)
    {
        if (_parent is { } parent)
        {
            lock (parent._gate) if (_active) parent.Backend(machine, backend);
            return;
        }
        // Comes just before the reachable it's part of: one change, not two.
        lock (_gate)
        {
            var index = Array.FindIndex(_items, item => item.Name == machine);
            if (index < 0 || _items[index].Reachable) Change(machine, item => item with { Backend = backend });
            else
            {
                var next = (MachineItem[])_items.Clone();
                next[index] = next[index] with { Backend = backend };
                _items = next;
            }
        }
        Welcomed?.Invoke(machine);
    }

    /// <summary>Each time a remote says hello: a good moment to check it, with no timer. Set once, on the root.</summary>
    public Action<string>? Welcomed { get; set; }

    /// <summary>The update check's latest answer for a machine; a null name is this machine.</summary>
    public void Updates(string? machine, IReadOnlyList<UpdateItem> updates)
    {
        lock (_gate)
            if (_items.FirstOrDefault(item => item.Name == machine) is not { } held || (held.Updates?.SequenceEqual(updates) ?? false)) return;
        Change(machine, item => item with { Updates = updates });
    }

    /// <summary>Whether the one update is running, and what it's doing or why it failed.</summary>
    public void Updating(string? machine, bool updating, string? note) => Change(machine, item => item with { Updating = updating, UpdateNote = note });

    private void Change(string? machine, Func<MachineItem, MachineItem> change)
    {
        lock (_gate)
        {
            var items = _items;
            var index = Array.FindIndex(items, item => item.Name == machine);
            if (index < 0) return;
            var changed = change(items[index]);
            if (changed == items[index]) return;
            var next = (MachineItem[])items.Clone();
            next[index] = changed;
            _items = next;
            topics.Publish(MachinesFeature.Topic, machine ?? "local", JsonSerializer.Serialize(changed, SessionsJson.Default.MachineItem));
        }
    }

    public void Reachable(string machine) => Set(machine, true, null);

    public void Unreachable(string machine, string reason) => Set(machine, false, reason);

    // A name it doesn't hold is ignored, and an unchanged item isn't swapped.
    private void Set(string machine, bool reachable, string? reason, bool checkedState = true)
    {
        if (_parent is { } parent)
        {
            lock (parent._gate)
                if (_active) parent.Set(machine, reachable, reason, checkedState);
            return;
        }
        lock (_gate)
        {
            var items = _items;
            var index = Array.FindIndex(items, item => item.Name == machine);
            if (index < 0 || (items[index].Checked == checkedState && items[index].Reachable == reachable && items[index].Reason == reason))
                return;
            var next = (MachineItem[])items.Clone();
            next[index] = items[index] with { Reachable = reachable, Checked = checkedState, Reason = reason };
            _items = next;
            topics.Publish(MachinesFeature.Topic, machine, JsonSerializer.Serialize(next[index], SessionsJson.Default.MachineItem));
        }
    }
}
