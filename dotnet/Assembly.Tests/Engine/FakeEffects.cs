using Assembly.Application.Features.Engine.Effects;

namespace Assembly.Tests.Engine;

internal sealed class FakeEffects : IEngineEffects
{
    private readonly HashSet<string> _successful = [];
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _success = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reconciled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstAttempt => _first.Task;
    public Task SecondAttempt => _second.Task;
    public Task ExternalSuccess => _success.Task;
    public Task Reconciled => _reconciled.Task;
    private readonly List<Effect> _attempts = [];
    public IReadOnlyList<Effect> Attempts => _attempts;
    private readonly List<string> _requestIds = [];
    public IReadOnlyList<string> RequestIds => _requestIds;
    private readonly List<string> _answeredRequestIds = [];
    public IReadOnlyList<string> AnsweredRequestIds => _answeredRequestIds;
    private readonly List<string> _executedIds = [];
    public IReadOnlyList<string> ExecutedIds => _executedIds;
    private readonly List<string> _reconciledIds = [];
    public IReadOnlyList<string> ReconciledIds => _reconciledIds;
    private readonly List<string> _unexpectedReplays = [];
    public IReadOnlyList<string> UnexpectedReplays => _unexpectedReplays;
    public required Func<Effect, CancellationToken, Task<EffectReceipt>> Execute { get; init; }
    public Func<Effect, CancellationToken, Task<EffectReceipt>>? Reconcile { get; init; }

    public async Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken)
    {
        lock (Attempts)
        {
            _attempts.Add(effect);
            if (_successful.Contains(effect.Id)) _unexpectedReplays.Add(effect.Id);
            _executedIds.Add(effect.Id);
            if (effect.Kind == "answer")
            {
                var request = effect.Payload.GetProperty("providerRequestId").GetString()!;
                if (!RequestIds.Contains(request)) _requestIds.Add(request);
            }
            if (Attempts.Count == 1) _first.TrySetResult();
            if (Attempts.Count == 2) _second.TrySetResult();
        }
        return await Execute(effect, cancellationToken);
    }

    public void SucceededExternally(Effect effect)
    {
        if (effect.Kind == "answer") _answeredRequestIds.Add(effect.Payload.GetProperty("providerRequestId").GetString()!);
        _successful.Add(effect.Id);
        _success.TrySetResult();
    }

    public async Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken)
    {
        _reconciledIds.Add(effect.Id);
        _reconciled.TrySetResult();
        return await Reconcile!(effect, cancellationToken);
    }
}
