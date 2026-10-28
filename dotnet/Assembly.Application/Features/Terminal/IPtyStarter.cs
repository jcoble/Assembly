namespace Assembly.Application.Features.Terminal;

/// <summary>
/// Starts a process on a new pseudo-terminal, as the leader of its own session and process group. Fails with the
/// folder's path when the folder doesn't exist or isn't a folder (terminal#1).
/// </summary>
public interface IPtyStarter
{
    IPty Start(PtyRequest request);
}
