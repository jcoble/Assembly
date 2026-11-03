using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Agents;

/// <summary>Owner-only, ephemeral challenge input on the same local sign-in board.</summary>
public sealed class ProvidersFeature(ChallengeBoard board, string localEngineMachine,
    Func<string, string, CallContext, Task<ChallengeReply?>>? readRemoteChallenge = null,
    Func<SubmitCodeArgs, CallContext, Task<ProviderInputReply>>? submitRemoteCode = null,
    Func<AcknowledgeUrlArgs, CallContext, Task<ProviderInputReply>>? acknowledgeRemoteUrl = null) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        var json = ProvidersJson.Default;
        calls.Read("providers", "challenge", json.ChallengeArgs, json.ChallengeReply, async (args, call) =>
        {
            RequireOwner(call, null);
            RequireOperation(args.OperationId);
            call.Cancellation.ThrowIfCancellationRequested();
            if (args.Machine is not null && args.Machine != localEngineMachine)
                return readRemoteChallenge is null ? throw Refusal("wrong_machine")
                    : (await readRemoteChallenge(args.Machine, args.OperationId, call))!;
            var current = board.Current(args.OperationId);
            return current is null ? null! : new ChallengeReply(current.OperationId, current.Generation,
                current.Link?.OriginalString, current.CallbackHost, current.CallbackPort, current.WantsCode, current.TerminalId);
        });
        calls.Read("providers", "submitCode", json.SubmitCodeArgs, json.ProviderInputReply, async (args, call) =>
        {
            RequireOwner(call, null);
            call.Cancellation.ThrowIfCancellationRequested();
            RequireOperation(args.OperationId);
            if (args.ExpectedGeneration <= 0) throw new CallFailed("The sign-in challenge is no longer available.", "stale_challenge");
            if (args.Code is null) throw Refusal();
            if (args.Machine is not null && args.Machine != localEngineMachine)
                return submitRemoteCode is null ? throw Refusal("wrong_machine")
                    : await submitRemoteCode(args, call);
            await InputAsync(() => board.SubmitAsync(args.OperationId, args.ExpectedGeneration, args.Code, call.Cancellation), call.Cancellation);
            return new ProviderInputReply();
        });
        calls.Read("providers", "acknowledgeUrl", json.AcknowledgeUrlArgs, json.ProviderInputReply, async (args, call) =>
        {
            RequireOwner(call, null);
            call.Cancellation.ThrowIfCancellationRequested();
            RequireOperation(args.OperationId);
            if (args.ExpectedGeneration <= 0) throw new CallFailed("The sign-in challenge is no longer available.", "stale_challenge");
            if (args.OriginalUrl is null) throw Refusal();
            if (args.Machine is not null && args.Machine != localEngineMachine)
                return acknowledgeRemoteUrl is null ? throw Refusal("wrong_machine")
                    : await acknowledgeRemoteUrl(args, call);
            await InputAsync(() => board.AcknowledgeUrlAsync(args.OperationId, args.ExpectedGeneration, args.OriginalUrl, call.Cancellation), call.Cancellation);
            return new ProviderInputReply();
        });
    }

    private void RequireOwner(CallContext call, string? machine)
    {
        if (call.Peer is not OwnerPeer) throw Refusal("forbidden");
        if (machine is not null && machine != localEngineMachine) throw Refusal("wrong_machine");
    }

    private static void RequireOperation(string operationId)
    {
        if (string.IsNullOrEmpty(operationId)) throw Refusal();
    }

    private static async Task InputAsync(Func<Task> deliver, CancellationToken token)
    {
        try { await deliver(); }
        catch (CallFailed error) when (error.Reason is "stale_challenge" or "challenge_consumed")
        { throw new CallFailed("The sign-in challenge is no longer available.", error.Reason); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Refusal(); }
    }

    private static CallFailed Refusal(string reason = "signin_input_failed") => new("Provider sign-in is unavailable.", reason);
}

public sealed record ChallengeArgs([property: JsonRequired] string OperationId, string? Machine = null);
public sealed record SubmitCodeArgs([property: JsonRequired] string OperationId, [property: JsonRequired] long ExpectedGeneration,
    [property: JsonRequired] string Code, string? Machine = null);
public sealed record AcknowledgeUrlArgs([property: JsonRequired] string OperationId, [property: JsonRequired] long ExpectedGeneration,
    [property: JsonRequired] string OriginalUrl, string? Machine = null);
public sealed record ChallengeReply(string OperationId, long Generation, string? Link, string? CallbackHost,
    int? CallbackPort, bool WantsCode, string? TerminalId);

public sealed record ProviderInputReply;
