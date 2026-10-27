using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>Where helpers start and messages go: the session's machine, this one or a remote one this machine reaches.</summary>
public sealed class HelperDelivery
{
    /// <summary>The machines, once they're set up; they need the sessions, which need this.</summary>
    public MachineSet? Machines { get; set; }

    private MachineSet Set => Machines ?? throw new InvalidOperationException("The machines aren't set up yet.");

    /// <summary>The machine with that name ("here" or none for this one), or null if there's none.</summary>
    public IMachine? Named(string? name) => Set.Named(name is null or "here" ? null : name);

    public IEnumerable<string> Names => ["here", .. Set.Remotes.Select(machine => machine.Name!)];

    /// <summary>Starts a turn on the session, or sends into the running one; null if it can't take the text.</summary>
    public Task<string?> SendAsync(SessionInfo session, string text, CancellationToken cancellationToken) =>
        Set.For(session).SendAsync(session, text, null, cancellationToken);
}
