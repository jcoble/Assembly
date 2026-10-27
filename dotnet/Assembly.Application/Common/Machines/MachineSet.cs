using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Common.Machines;

/// <summary>This machine, and the remote machines whose sessions it shows. A session's <c>Machine</c> picks one.</summary>
public sealed class MachineSet(IMachine local, params IReadOnlyList<IMachine> remotes)
{
    public IReadOnlyList<IMachine> Remotes => remotes;

    public IMachine For(SessionInfo session) =>
        Named(session.Machine) ?? throw new MachineUnreachable($"{session.Machine} isn't set up here");

    /// <summary>The machine with that name, this one for null, or null if there's none.</summary>
    public IMachine? Named(string? name) => name is null ? local : remotes.FirstOrDefault(machine => machine.Name == name);
}
