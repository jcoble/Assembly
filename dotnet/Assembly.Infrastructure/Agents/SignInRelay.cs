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
        Validate(machine, operationId, ct);
        var args = JsonSerializer.SerializeToElement(new ChallengeArgs(operationId), ProvidersJson.Default.ChallengeArgs);
        try
        {
            var challenge = await _forward.CallAsync(machine, "providers", "challenge", args, false, ct);
            return challenge.ValueKind == JsonValueKind.Null ? null : challenge;
        }
        catch (CallFailed error) { throw SafeRefusal(error); }
    }

    public async Task SubmitCodeAsync(string machine, string operationId, long expectedGeneration, string code, CancellationToken ct)
    {
        Validate(machine, operationId, ct);
        if (expectedGeneration <= 0 || code is null) throw Refusal();
        // Machine is consumed by the existing connection resolver, never supplied as owner-route authority.
        var args = JsonSerializer.SerializeToElement(new SubmitCodeArgs(operationId, expectedGeneration, code),
            ProvidersJson.Default.SubmitCodeArgs);
        try { await _forward.CallAsync(machine, "providers", "submitCode", args, false, ct); }
        catch (CallFailed error) { throw SafeRefusal(error); }
    }

    public async Task AcknowledgeUrlAsync(string machine, string operationId, long expectedGeneration, string originalUrl, CancellationToken ct)
    {
        Validate(machine, operationId, ct);
        if (expectedGeneration <= 0 || originalUrl is null) throw Refusal();
        var args = JsonSerializer.SerializeToElement(new AcknowledgeUrlArgs(operationId, expectedGeneration, originalUrl),
            ProvidersJson.Default.AcknowledgeUrlArgs);
        try { await _forward.CallAsync(machine, "providers", "acknowledgeUrl", args, false, ct); }
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
        "forbidden" or "not_found" or "cancelled" or "timeout" or "bad_args" or "wrong_machine" or "failed" or "signin_input_failed"
            => new("Provider sign-in is unavailable.", error.Reason),
        _ => Refusal()
    };
}
