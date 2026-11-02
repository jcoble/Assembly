using Assembly.Application.Common.Engine;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Sessions;

public enum SteerResult { Injected, PromptRequired, NotRunning }
public abstract record SendResult
{
    public sealed record Inserted(string RunId, string ProviderMessageId, UserRowReference? UserRow = null) : SendResult;
    public sealed record Busy : SendResult;
    public sealed record AuthRequired(UserRowReference? UserRow = null) : SendResult;
    public sealed record Failed(string Reason) : SendResult;
    public sealed record Unknown : SendResult;
}

internal sealed record OwningSendAdmission(string EffectId, string MessageId, int Attempt,
    string SessionId, string ClientId, string Text, string? SenderLabel, long Epoch,
    UserRowReference? UserRow, IReadOnlyList<string> AttachmentIds, string? PriorEffectId, string? FromSessionId = null);

internal sealed record SteerDeliveryResult(SteerResult Outcome, UserRowReference? UserRow = null);
