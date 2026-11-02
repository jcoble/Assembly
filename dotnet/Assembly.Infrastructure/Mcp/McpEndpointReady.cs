using System.Text.Json;
using Assembly.Engine.Mcp;

namespace Assembly.Infrastructure.Mcp;

/// <summary>The listener's one-shot startup barrier; stopped listeners cannot issue server entries.</summary>
internal sealed class McpEndpointReady(McpTokens tokens)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _listening, _stopped;
    public Task Ready => _ready.Task;

    public async Task WaitAsync(CancellationToken ct)
    {
        await Ready.WaitAsync(ct);
        RequireListening();
    }

    public void RequireListening()
    {
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException();
            if (!_listening) throw new InvalidOperationException("MCP not listening");
        }
    }

    public void Listening(Uri address)
    {
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException();
            if (_listening) throw new InvalidOperationException("MCP already listening");
            tokens.Url = new Uri(address, "/mcp").ToString();
            _listening = true;
            _ready.SetResult();
        }
    }

    public IReadOnlyList<JsonElement> ServersFor(string id, long epoch)
    {
        lock (_gate)
        {
            RequireListening();
            return tokens.ServersFor(id, epoch);
        }
    }

    public void Stop()
    {
        lock (_gate) { _stopped = true; _ready.TrySetCanceled(); }
    }
}
