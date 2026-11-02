using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Agents;

public sealed record Challenge(string OperationId, long Generation, Uri? Link, string? CallbackHost,
    int? CallbackPort, bool WantsCode, string? TerminalId);
public interface IChallengeInput { ValueTask SubmitAsync(string code, CancellationToken ct); }

/// <summary>One ephemeral owner for exact-generation input and explicit URL consent. Nothing here is persisted.</summary>
public sealed class ChallengeBoard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _operations = [];
    private long _generation;
    public event Action<string>? Changed;

    public Challenge Post(string operationId, Uri? link, string? callbackHost, int? callbackPort,
        bool wantsCode, string? terminalId, IChallengeInput? input)
    {
        Pending? old;
        Challenge challenge;
        lock (_gate)
        {
            _operations.TryGetValue(operationId, out old);
            challenge = new(operationId, checked(++_generation), link, callbackHost, callbackPort, wantsCode, terminalId);
            _operations[operationId] = new(challenge, input, null);
        }
        old?.Stop();
        Changed?.Invoke(operationId);
        return challenge;
    }

    public Challenge? Current(string operationId)
    {
        lock (_gate) return _operations.GetValueOrDefault(operationId)?.Challenge;
    }

    public async Task SubmitAsync(string operationId, long expectedGeneration, string code, CancellationToken ct)
    {
        Pending pending;
        IChallengeInput input;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            pending = Require(operationId, expectedGeneration);
            input = pending.Input ?? throw Refusal("stale_challenge");
            pending.Consumed = true;
            pending.Challenge = null;
            pending.Input = null;
            pending.Delivering = true;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pending.Lifetime.Token);
            await input.SubmitAsync(code, linked.Token);
        }
        finally
        {
            pending.Lifetime.Dispose();
            Changed?.Invoke(operationId);
        }
    }

    public async ValueTask<bool> RequestUrlConsentAsync(string operationId, Uri originalUrl, CancellationToken ct)
    {
        Pending pending;
        Pending? old;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            _operations.TryGetValue(operationId, out old);
            var callback = SignInMethods.Callback(originalUrl);
            var challenge = new Challenge(operationId, checked(++_generation), originalUrl,
                callback?.Host, callback?.Port, false, null);
            pending = new(challenge, null, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _operations[operationId] = pending;
        }
        old?.Stop();
        using var registration = ct.Register(() => CancelOwned(operationId, pending));
        Changed?.Invoke(operationId);
        try { return await pending.Consent!.Task; }
        finally { pending.Lifetime.Dispose(); }
    }

    public Task AcknowledgeUrlAsync(string operationId, long expectedGeneration, string originalUrl, CancellationToken ct)
    {
        Pending pending;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            pending = Require(operationId, expectedGeneration);
            if (pending.Consent is null || pending.Challenge?.Link?.OriginalString != originalUrl)
                throw Refusal("stale_challenge");
            pending.Consumed = true;
            pending.Challenge = null;
            pending.Consent.TrySetResult(true);
        }
        Changed?.Invoke(operationId);
        return Task.CompletedTask;
    }

    public void Clear(string operationId)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_operations.Remove(operationId, out pending)) return;
        }
        pending.Stop();
        Changed?.Invoke(operationId);
    }

    private void CancelOwned(string operationId, Pending expected)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var current) || current != expected) return;
            _operations.Remove(operationId);
        }
        expected.Stop();
        Changed?.Invoke(operationId);
    }

    private Pending Require(string operationId, long generation)
    {
        if (!_operations.TryGetValue(operationId, out var pending) || pending.Generation != generation)
            throw Refusal("stale_challenge");
        if (pending.Consumed) throw Refusal("challenge_consumed");
        return pending;
    }

    private static CallFailed Refusal(string reason) => new("The sign-in challenge is no longer available.", reason);

    private sealed class Pending(Challenge challenge, IChallengeInput? input, TaskCompletionSource<bool>? consent)
    {
        public long Generation { get; } = challenge.Generation;
        public Challenge? Challenge { get; set; } = challenge;
        public IChallengeInput? Input { get; set; } = input;
        public TaskCompletionSource<bool>? Consent { get; } = consent;
        public CancellationTokenSource Lifetime { get; } = new();
        public bool Consumed { get; set; }
        public bool Delivering { get; set; }
        public void Stop()
        {
            Challenge = null;
            Input = null;
            Consent?.TrySetResult(false);
            try { Lifetime.Cancel(); }
            catch (ObjectDisposedException) { /* An already-completed captured delivery owns disposal. */ }
            if (!Delivering && Consent is null) Lifetime.Dispose();
        }
    }
}
