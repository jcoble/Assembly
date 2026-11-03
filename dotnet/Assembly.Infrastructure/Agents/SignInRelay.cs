using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Agents;

/// <summary>Ephemeral challenge calls over the selected engine's existing owner connection.</summary>
public sealed class SignInRelay
{
    private readonly MachineForward _forward;

    internal SignInRelay(MachineForward forward) => _forward = forward;

    public async Task<JsonElement?> ChallengeAsync(string machine, string operationId, CancellationToken ct)
    {
        var call = new CallContext(ct, "");
        try
        {
            var reply = await ReadChallengeAsync(machine, operationId, call);
            return reply is null ? null : JsonSerializer.SerializeToElement(reply, ProvidersJson.Default.ChallengeReply);
        }
        finally { call.SourceGuard?.Dispose(); }
    }

    internal async Task<ChallengeReply?> ReadChallengeAsync(string machine, string operationId, CallContext call)
    {
        Validate(machine, operationId, call.Cancellation);
        try { return await _forward.SignInChallengeAsync(machine, operationId, call); }
        catch (CallFailed error) { throw SafeRefusal(error); }
    }

    public async Task SubmitCodeAsync(string machine, string operationId, long expectedGeneration, string code, CancellationToken ct)
    {
        var call = new CallContext(ct, "");
        try { await SubmitCodeAsync(new(operationId, expectedGeneration, code, machine), call); }
        finally { call.SourceGuard?.Dispose(); }
    }

    internal async Task<ProviderInputReply> SubmitCodeAsync(SubmitCodeArgs args, CallContext call)
    {
        Validate(args.Machine!, args.OperationId, call.Cancellation);
        if (args.ExpectedGeneration <= 0 || args.Code is null) throw Refusal();
        try { return await _forward.SignInCodeAsync(args, call); }
        catch (CallFailed error) { throw SafeRefusal(error); }
    }

    public async Task AcknowledgeUrlAsync(string machine, string operationId, long expectedGeneration, string originalUrl, CancellationToken ct)
    {
        var call = new CallContext(ct, "");
        try { await AcknowledgeUrlAsync(new(operationId, expectedGeneration, originalUrl, machine), call); }
        finally { call.SourceGuard?.Dispose(); }
    }

    internal async Task<ProviderInputReply> AcknowledgeUrlAsync(AcknowledgeUrlArgs args, CallContext call)
    {
        Validate(args.Machine!, args.OperationId, call.Cancellation);
        if (args.ExpectedGeneration <= 0 || args.OriginalUrl is null) throw Refusal();
        try { return await _forward.SignInUrlAsync(args, call); }
        catch (CallFailed error) { throw SafeRefusal(error); }
    }

    private static void Validate(string machine, string operationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrEmpty(operationId)) throw Refusal();
    }

    private static CallFailed Refusal() => new("Provider sign-in is unavailable.", "signin_input_failed");

    private static CallFailed SafeRefusal(CallFailed error) => error.Reason switch
    {
        "stale_challenge" or "challenge_consumed" => new("The sign-in challenge is no longer available.", error.Reason),
        "source_changed" or "forbidden" or "not_found" or "cancelled" or "timeout" or "bad_args" or "wrong_machine" or "failed" or "signin_input_failed"
            => new("Provider sign-in is unavailable.", error.Reason),
        _ => Refusal()
    };
}
