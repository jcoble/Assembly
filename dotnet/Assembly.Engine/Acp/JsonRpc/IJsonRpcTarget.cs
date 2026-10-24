using System.Text.Json;

namespace Assembly.Engine.Acp.JsonRpc;

/// <summary>Receives what the other side sends us: requests (which need an answer), notifications, and the close.</summary>
public interface IJsonRpcTarget
{
    /// <summary>Answer a request. Throw <see cref="JsonRpcException"/> to send an error back.</summary>
    Task<JsonElement> OnRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken);

    /// <summary>Called on the read loop, in arrival order. Must not block.</summary>
    void OnNotification(string method, JsonElement parameters);

    /// <summary>Called once when the read loop ends, for any reason.</summary>
    void OnClosed(Exception reason);
}
