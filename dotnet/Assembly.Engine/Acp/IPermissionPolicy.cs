using Assembly.Engine.Acp.Messages;

namespace Assembly.Engine.Acp;

/// <summary>
/// Answers what the agent asks the user: a <c>session/request_permission</c>, or an <c>elicitation/create</c> form
/// (a question with choices). The token is cancelled when the turn is cancelled or the agent goes.
/// </summary>
public interface IPermissionPolicy
{
    Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken);

    /// <summary>Without a person to ask, a question is cancelled, which ends just the asking tool call.</summary>
    Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ElicitationResponse.Cancel);
}
