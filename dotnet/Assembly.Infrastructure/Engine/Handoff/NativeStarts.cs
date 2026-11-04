using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Runs an admitted start through the real native resume and reports exactly what it proved.</summary>
internal static class NativeStarts
{
    internal static async Task<EffectReceipt> ExecuteAsync(Effect effect, SessionRegistry hosts, Action validate, CancellationToken ct)
    {
        var start = effect.Payload.Deserialize(EngineJson.Default.SessionStartEffect)!;
        var attempt = hosts.TryAcquire(effect.SessionId!);
        if (attempt.Lease is null) return Refused(EffectDisposition.Unknown, "busy");
        await using var lease = attempt.Lease;
        try
        {
            validate();
            if (!Directory.Exists(start.Folder)) return Ended(start, ResumeReason.NoFolder);
            var evidence = await hosts.For(effect.SessionId!).ResumeNativeForMaintenanceAsync(start.ProviderSessionId!, start.Folder, validate, ct);
            validate();
            return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
                new SessionEffectData("idle", start.ProviderSessionId, null, evidence), EngineJson.Default.SessionEffectData), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failure(error) switch
            {
                ResumeReason.Transient => Refused(EffectDisposition.TransientFailure, ResumeReason.Transient),
                { } reason => Ended(start, reason),
                null => Refused(EffectDisposition.Unknown, "native_resume_unconfirmed")
            };
        }
    }

    private static EffectReceipt Refused(EffectDisposition disposition, string reason) =>
        new(disposition, JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), reason);

    // A permanent refusal is the operation's real result; it ends this start with that typed reason.
    private static EffectReceipt Ended(SessionStartEffect start, string reason) => new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
        new SessionEffectData("ended", start.ProviderSessionId, reason), EngineJson.Default.SessionEffectData), null);

    private static string? Failure(Exception error) => error switch
    {
        HandoffRefused { Message: ResumeReason.Missing or ResumeReason.Unsupported or ResumeReason.NoFolder } refused => refused.Message,
        HandoffRefused => ResumeReason.WriterElsewhere,
        CallFailed { Reason: ResumeReason.Missing or ResumeReason.Unsupported or ResumeReason.NoFolder or ResumeReason.WriterElsewhere } failed => failed.Reason,
        _ => AdapterEffects.ResumeFailureReason(error)
    };
}
