using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Agents;

/// <summary>Ephemeral challenge calls over the selected engine's existing owner connection.</summary>
public sealed partial class SignInRelay
{
    private readonly MachineForward _forward;


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
        try
        {
            var scope = await ScopeAsync(machine, operationId, call.Cancellation);
            var reply = await scope.ReadAsync(call.Cancellation);
            scope.Guard(call, challenge: true, expected: reply);
            return reply;
        }
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
        try
        {
            var scope = await ScopeAsync(args.Machine!, args.OperationId, call.Cancellation);
            await scope.ReadAsync(call.Cancellation);
            scope.EnsureInput(args.ExpectedGeneration);
            var reply = await _forward.SignInCodeAsync(args, call, scope.Target);
            scope.Guard(call);
            return reply;
        }
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
        try
        {
            var scope = await ScopeAsync(args.Machine!, args.OperationId, call.Cancellation);
            await scope.ReadAsync(call.Cancellation);
            scope.EnsureInput(args.ExpectedGeneration);
            var reply = await _forward.SignInUrlAsync(args, call, scope.Target);
            scope.Guard(call);
            return reply;
        }
        catch (CallFailed error) { throw SafeRefusal(error); }
    }

    private static void Validate(string machine, string operationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrEmpty(operationId)) throw Refusal();
    }

    private static CallFailed Refusal() => new("Provider sign-in is unavailable.", "signin_input_failed");

    private static string UnknownCopy(string cause) => cause + " Original cancellation is unconfirmed; the attempt may remain active until its remote deadline.";

    private static CallFailed CancellationUnconfirmed(string cause) => new(UnknownCopy(cause switch
    {
        "source_changed" => "The sign-in source changed.",
        "signin_callback_port_in_use" => "The sign-in callback port is already in use.",
        "signin_callback_lost" => "The sign-in callback connection was lost.",
        _ => "The sign-in callback connection is unavailable."
    }), cause == "source_changed" ? "source_changed" : "signin_cancel_unconfirmed");

    private static string SafeUnknownCopy(string value)
    {
        foreach (var cause in new[] { "signin_callback_port_in_use", "signin_callback_lost", "signin_callback_unavailable" })
            if (value == CancellationUnconfirmed(cause).Message) return value;
        return CancellationUnconfirmed("signin_callback_unavailable").Message;
    }

    private static CallFailed SafeRefusal(CallFailed error) => error.Reason switch
    {
        "stale_challenge" or "challenge_consumed" => new("The sign-in challenge is no longer available.", error.Reason),
        "signin_callback_port_in_use" => new("The sign-in callback port is already in use.", error.Reason),
        "signin_callback_lost" => new("The sign-in callback connection was lost.", error.Reason),
        "signin_callback_unavailable" => new("The sign-in callback connection is unavailable.", error.Reason),
        "signin_cancel_unconfirmed" => new(SafeUnknownCopy(error.Message), error.Reason),
        "source_changed" => new(error.Message == UnknownCopy("The sign-in source changed.")
            ? error.Message : "The sign-in source changed.", error.Reason),
        "forbidden" or "not_found" or "cancelled" or "timeout" or "bad_args" or "wrong_machine" or "failed" or "signin_input_failed"
            => new("Provider sign-in is unavailable.", error.Reason),
        _ => Refusal()
    };
}
