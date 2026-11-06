using Assembly.Domain.Markdown;
using System.Text.Json.Serialization;

namespace Assembly.Domain.Rows;

/// <summary>A finished message.</summary>
public sealed record MessagePayload(IReadOnlyList<SafeBlock> Blocks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null);
