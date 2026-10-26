using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

namespace Assembly.Application.Common.Machines;

/// <summary>This machine, and the remote machines whose threads it shows. A thread's <c>Machine</c> picks one.</summary>
public sealed class MachineSet(IMachine local, params IReadOnlyList<IMachine> remotes)
{
    public IReadOnlyList<IMachine> Remotes => remotes;

    public IMachine For(ThreadInfo thread) =>
        Named(thread.Machine) ?? throw new MachineUnreachable($"{thread.Machine} isn't set up here");

    /// <summary>The machine with that name, this one for null, or null if there's none.</summary>
    public IMachine? Named(string? name) => name is null ? local : remotes.FirstOrDefault(machine => machine.Name == name);
}
