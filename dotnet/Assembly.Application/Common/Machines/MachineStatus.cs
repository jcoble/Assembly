using System.Text.Json;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Common.Machines;

/// <summary>
/// One machine as the <c>machines</c> topic sends it, keyed by its name (<c>local</c> for this machine):
/// <paramref name="Name"/> is null for this machine, <paramref name="Kind"/> is <c>local</c> or <c>remote</c>,
/// <paramref name="Reason"/> describes connection, backend version or deployment warnings, and <paramref name="Checked"/> is false for a remote until its
/// first probe answers or fails (until then it's neither up nor down).
/// </summary>
public sealed record MachineItem(string? Name, string Kind, bool Reachable, string? Reason, bool Checked, string? Ssh = null, int? Port = null, string? TokenFile = null);

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

    private readonly Dictionary<string, string?> _connectionReasons = [];
    private readonly Dictionary<string, string?> _backendMismatches = [];
    private readonly Dictionary<string, string?> _deployments = [];

    public void BackendMismatch(string machine, string? reason) => Warning(machine, reason, false);
    public void Deployment(string machine, string? reason) => Warning(machine, reason, true);

    private void Warning(string machine, string? reason, bool deployment)
    {
        if (_parent is { } parent)
        {
            lock (parent._gate) if (_active) parent.Warning(machine, reason, deployment);
            return;
        }
        lock (_gate)
        {
            var warnings = deployment ? _deployments : _backendMismatches;
            if (warnings.GetValueOrDefault(machine) == reason) return;
            warnings[machine] = reason;
            var item = _items.FirstOrDefault(item => item.Name == machine);
            if (item is not null) Set(machine, item.Reachable, _connectionReasons.GetValueOrDefault(machine), item.Checked);
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
            _connectionReasons[machine] = reason;
            reason = string.Join(" ", new[] { reason, _backendMismatches.GetValueOrDefault(machine), _deployments.GetValueOrDefault(machine) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (reason.Length == 0) reason = null;
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
