using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.JsonRpc;
using System.Text.Json;

namespace Assembly.Application.Common.Acp;

/// <summary>
/// Answers what the agent asks the user: a <c>session/request_permission</c>, or an <c>elicitation/create</c> form
/// (a question with choices). The token is cancelled when the turn is cancelled or the agent goes.
/// </summary>
public interface IPermissionPolicy
{
    bool AllowAutomaticBoardUpdates => true;
    Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken);

    /// <summary>Without a person to ask, a question is cancelled, which ends just the asking tool call.</summary>
    Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ElicitationResponse.Cancel);
    Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, JsonRpcRequestContext context,
        CancellationToken cancellationToken) => DecideAsync(request, cancellationToken);
    Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, JsonRpcRequestContext context,
        CancellationToken cancellationToken) => ElicitAsync(request, cancellationToken);
    ValueTask<bool> ElicitUrlAsync(string? sessionId, Uri url, string host, CancellationToken ct) => ValueTask.FromResult(false);
    void Cancelling(string actualAcpSessionId) { }
    void Decided(string method, string acpSessionId, JsonRpcRequestContext context, JsonElement outcome) { }
}
