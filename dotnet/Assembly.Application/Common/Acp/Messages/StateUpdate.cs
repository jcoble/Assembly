using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

public sealed record StateUpdate(string State, string? StopReason = null, AcpError? Error = null,
    UsageReport? Usage = null, IReadOnlyList<BackgroundActivity>? Background = null,
    [property: JsonPropertyName("_meta")] JsonElement? Meta = null)
{
    // Both adapters also carry token totals in their quota extension. Codex 2.5.0 uses this at protocol 2.
    [JsonIgnore]
    public UsageReport? ReportedUsage
    {
        get
        {
            if (Usage is not null) return Usage;
            if (Meta is not { ValueKind: JsonValueKind.Object } meta
                || !meta.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object
                || !quota.TryGetProperty("token_count", out var count) || count.ValueKind != JsonValueKind.Object) return null;
            return new UsageReport(Number(count, "inputTokens"), Number(count, "outputTokens"),
                Number(count, "cachedInputTokens"), Number(count, "cacheWriteInputTokens") ?? Number(count, "cachedWriteTokens"));
        }
    }

    private static long? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var number) && number.TryGetInt64(out var count) ? count : null;
}

public sealed record UsageReport(long? InputTokens = null, long? OutputTokens = null,
    long? CachedReadTokens = null, long? CachedWriteTokens = null, decimal? CostUsd = null,
    string? Model = null, long? Used = null, long? Size = null);

public sealed record BackgroundActivity(string Id, string? Title = null, string? State = null, string? ToolCallId = null);
