using System.Collections.Concurrent;
using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V1;
using Assembly.Application.Common.Acp.V2;

namespace Assembly.Application.Common.Acp;

/// <summary>
/// A version 1 connection in the version 2 shape. Version 1 has no message id, no insertion and no end-of-turn
/// update: the reply to <c>session/prompt</c> ends the turn. So the client makes up the id, counts the prompt as taken
/// at the agent's first sign of life (<see cref="SignOfLife"/>), and ends the turn from the reply.
/// </summary>
public sealed partial class AcpClient
{
    /// <summary>Made-up message ids start with this, so they never match an agent's own id.</summary>
    public const string Version1MessageIdPrefix = "v1:";

    private int _version = AcpProtocol.Version;
    private sealed record UntakenPrompt(Foreground Foreground, Foreground.Pending Pending, string MessageId);
    // Sent but not yet taken, by session. A version 1 agent runs one prompt per session at a time.
    private readonly ConcurrentDictionary<string, UntakenPrompt> _untaken = new();
    private readonly OpenTools _openTools = new();

    private PromptRun PromptV1(string sessionId, IReadOnlyList<ContentBlock> prompt, CancellationToken cancellationToken)
    {
        var foreground = For(sessionId);
        var pending = foreground.Begin();
        var untaken = new UntakenPrompt(foreground, pending, Version1MessageIdPrefix + Guid.NewGuid().ToString("N"));
        _untaken[sessionId] = untaken;
        _prompts.Start("ACP prompt " + sessionId, SendPromptAsync);
        return new PromptRun(pending.Inserted.Task, pending.Ended.Task);

        async Task SendPromptAsync(CancellationToken closing)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing);
            try
            {
                // The reply hook runs in pipe order, after the turn's last update.
                await _rpc.RequestAsync(AcpMethods.SessionPrompt,
                    JsonSerializer.SerializeToElement(new PromptRequest(sessionId, prompt), AcpJsonContext.Default.PromptRequest),
                    lifetime.Token, result =>
                    {
                        Taken(sessionId);
                        var reply = result.Deserialize(AcpJsonContext.Default.PromptReply);
                        var stopReason = reply?.StopReason ?? "end_turn";
                        CloseOpenTools(foreground, sessionId, stopReason);
                        foreground.EndFromReply(pending, new PromptResult(untaken.MessageId, stopReason, null, Uncached(reply?.Usage)));
                    },
                    // Fenced against the reader like the reply; after a reply the tools are already closed.
                    onSettled: () => CloseOpenTools(foreground, sessionId, "error"));
            }
            catch (Exception error)
            {
                // Not taken before the error: the prompt stays "not inserted", so a sign-in can retry it.
                _untaken.TryRemove(new KeyValuePair<string, UntakenPrompt>(sessionId, untaken));
                foreground.Failed(pending, error);
            }
        }
    }

    private void CloseOpenTools(Foreground foreground, string sessionId, string stopReason)
    {
        foreach (var closing in _openTools.Close(sessionId, stopReason))
            foreground.Read(closing, () => PublishAccepted(closing));
    }

    /// <summary>Version 1 counts cached reads and writes inside its input; version 2's input is only the uncached part.</summary>
    private static UsageReport? Uncached(UsageReport? usage) => usage is { InputTokens: { } input }
        ? usage with { InputTokens = Math.Max(0, input - (usage.CachedReadTokens ?? 0) - (usage.CachedWriteTokens ?? 0)) }
        : usage;

    private void Taken(string sessionId)
    {
        if (_untaken.TryRemove(sessionId, out var untaken))
            untaken.Foreground.Inserted(untaken.Pending, untaken.MessageId);
    }

    private void OnUpdateV1(SessionNotification received)
    {
        if (_resuming.ContainsKey(received.SessionId)) return; // replay text is not live traffic
        _openTools.Read(received);
        var update = UpdateNormalizer.Normalize(received);
        if (SignOfLife.Counts(received.Kind)) Taken(update.SessionId);
        if (_foreground.TryGetValue(update.SessionId, out var foreground))
            foreground.Read(update, () => PublishAccepted(update));
        else PublishAccepted(update);
    }

    /// <summary>The reply to the cancelled prompt ends the wait; a cancel after that reply has nothing to wait for.</summary>
    private async Task CancelV1Async(string sessionId, CancellationToken cancellationToken)
    {
        var foreground = For(sessionId);
        var waiting = foreground.Cancelling();
        try
        {
            if (!foreground.Running) return;
            await _rpc.NotifyAsync(AcpMethods.SessionCancel,
                JsonSerializer.SerializeToElement(new CancelNotification(sessionId), AcpJsonContext.Default.CancelNotification), cancellationToken);
            await waiting.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            foreground.RemoveCancel(waiting);
        }
    }
}
