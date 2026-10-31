namespace Assembly.Infrastructure.Socket;

/// <summary>A subscription owns its work and queued frames until it closes.</summary>
public interface ISocketSubscription : IDisposable
{
    CancellationToken Closed { get; }
    Task Completion { get; }
}
