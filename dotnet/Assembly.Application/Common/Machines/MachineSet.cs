using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Common.Machines;

/// <summary>This machine, and the remote machines whose sessions it shows. A session's <c>Machine</c> picks one.</summary>
public sealed class MachineSet(IMachine local, params IReadOnlyList<IMachine> remotes)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IMachine> _remotes = remotes.ToDictionary(machine => machine.Name!);
    public IReadOnlyList<IMachine> Remotes { get { lock (_gate) return [.. _remotes.Values]; } }
    public void Set(IMachine machine) { lock (_gate) _remotes[machine.Name!] = machine; }
    public void Remove(string name) { lock (_gate) _remotes.Remove(name); }

    public IMachine For(SessionInfo session) =>
        Named(session.Machine) ?? throw new MachineUnreachable(session.Machine!, $"{session.Machine} isn't set up here");

    /// <summary>The machine with that name, this one for null, or null if there's none.</summary>
    public IMachine? Named(string? name) => name is null ? local : Remote(name);

    private IMachine? Remote(string name) { lock (_gate) return _remotes.GetValueOrDefault(name); }
}
