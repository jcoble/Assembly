using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Agents;

namespace Assembly.Tests.Features.Agents;

public sealed class ChallengeBoardTests
{
    [Fact]
    public async Task Replaced_code_generation_is_stale_and_current_is_consumed_once()
    {
        var board = new ChallengeBoard();
        var old = new Input();
        var current = new Input();
        var first = board.Post("op", null, null, null, true, "terminal", old);
        var second = board.Post("op", null, null, null, true, "terminal", current);
        Assert.True(second.Generation > first.Generation);
        Assert.Equal("stale_challenge", (await Assert.ThrowsAsync<CallFailed>(() => board.SubmitAsync("op", first.Generation, "OLD", default))).Reason);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await board.SubmitAsync("op", second.Generation, "CODE", default); return true; }
            catch (CallFailed error) { Assert.Equal("challenge_consumed", error.Reason); return false; }
        }));
        Assert.Equal(1, outcomes.Count(value => value));
        Assert.Empty(old.Codes);
        Assert.Equal(["CODE"], current.Codes);
        board.Clear("op");
        Assert.Null(board.Current("op"));
    }

    [Fact]
    public async Task Url_consent_requires_exact_current_generation_and_original_string()
    {
        var board = new ChallengeBoard();
        var uri = new Uri("https://login.example/auth?code=%2f&state=SECRET");
        var first = board.RequestUrlConsentAsync("op", uri, default).AsTask();
        var challenge = board.Current("op")!;
        Assert.Equal(uri.OriginalString, challenge.Link!.OriginalString);
        Assert.False(first.IsCompleted);
        Assert.Equal("stale_challenge", (await Assert.ThrowsAsync<CallFailed>(() => board.AcknowledgeUrlAsync("op", challenge.Generation, uri.AbsoluteUri + "x", default))).Reason);
        var second = board.RequestUrlConsentAsync("op", uri, default).AsTask();
        Assert.False(await first);
        var replacement = board.Current("op")!;
        Assert.True(replacement.Generation > challenge.Generation);
        Assert.Equal("stale_challenge", (await Assert.ThrowsAsync<CallFailed>(() => board.AcknowledgeUrlAsync("op", challenge.Generation, uri.OriginalString, default))).Reason);
        Assert.False(second.IsCompleted);
        await board.AcknowledgeUrlAsync("op", replacement.Generation, uri.OriginalString, default);
        Assert.True(await second);
        Assert.Equal("challenge_consumed", (await Assert.ThrowsAsync<CallFailed>(() => board.AcknowledgeUrlAsync("op", replacement.Generation, uri.OriginalString, default))).Reason);
    }

    [Fact]
    public async Task Cancellation_and_clear_complete_only_the_owned_url_waiter()
    {
        var board = new ChallengeBoard();
        using var cancel = new CancellationTokenSource();
        var cancelled = board.RequestUrlConsentAsync("cancel", new Uri("https://a.example"), cancel.Token).AsTask();
        var other = board.RequestUrlConsentAsync("other", new Uri("https://b.example"), default).AsTask();
        cancel.Cancel();
        Assert.False(await cancelled);
        Assert.Null(board.Current("cancel"));
        Assert.False(other.IsCompleted);
        board.Clear("other");
        Assert.False(await other);
        Assert.Null(board.Current("other"));
    }

    [Fact]
    public async Task Clear_cancels_an_already_captured_code_delivery_without_redirecting_it()
    {
        var board = new ChallengeBoard();
        var input = new Input { Wait = true };
        var challenge = board.Post("op", null, null, null, true, "terminal", input);
        var delivery = board.SubmitAsync("op", challenge.Generation, "CODE", default);
        await input.Entered.Task;
        board.Clear("op");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Null(board.Current("op"));
    }

    private sealed class Input : IChallengeInput
    {
        public List<string> Codes { get; } = [];
        public bool Wait { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SubmitAsync(string code, CancellationToken ct)
        {
            Entered.TrySetResult();
            if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            Codes.Add(code);
        }
    }
}
