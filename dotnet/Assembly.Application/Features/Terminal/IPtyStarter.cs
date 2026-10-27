namespace Assembly.Application.Features.Terminal;

/// <summary>Starts a process on a new pseudo-terminal, as the leader of its own session and process group.</summary>
public interface IPtyStarter
{
    IPty Start(PtyRequest request);
}
