using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Engine.Mcp;

/// <summary>The original request lifetime, separate from the token for admitted tool execution.</summary>
internal static class McpRequestWait
{
    private const string Key = "assembly.internal.original-request-wait/v1";

    internal static IDisposable Capture(RequestContext<CallToolRequestParams> request, CancellationToken original)
    {
        var items = request.Items;
        var hadPrevious = items.TryGetValue(Key, out var previous);
        var holder = new TokenHolder(original);
        items[Key] = holder;
        return new CaptureScope(items, holder, hadPrevious, previous);
    }

    internal static CancellationToken Require(RequestContext<CallToolRequestParams> request) =>
        request.Items.TryGetValue(Key, out var value) && value is TokenHolder holder
            ? holder.Token
            : throw new InvalidOperationException("The MCP request pipeline did not capture its original wait token.");

    private sealed record TokenHolder(CancellationToken Token);

    private sealed class CaptureScope(IDictionary<string, object?> items, TokenHolder holder, bool hadPrevious, object? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!items.TryGetValue(Key, out var current) || !ReferenceEquals(current, holder)) return;
            if (hadPrevious) items[Key] = previous;
            else items.Remove(Key);
        }
    }
}
