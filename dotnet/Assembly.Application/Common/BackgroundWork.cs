namespace Assembly.Application.Common;

/// <summary>Compile stub, replaced in the next commit.</summary>
public sealed class BackgroundWork : IAsyncDisposable
{
    public BackgroundWork(TimeProvider time, TextWriter log)
    {
    }

    public int Running => 0;

    public void Start(string name, Func<CancellationToken, Task> work)
    {
    }

    public Task<int> StopAsync(TimeSpan deadline) => Task.FromResult(0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
