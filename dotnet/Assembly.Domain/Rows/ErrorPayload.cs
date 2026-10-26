using System.Text.Json.Serialization;

namespace Assembly.Domain.Rows;

public sealed record ErrorPayload(
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Code);
