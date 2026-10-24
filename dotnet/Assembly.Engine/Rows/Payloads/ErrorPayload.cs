using System.Text.Json.Serialization;

namespace Assembly.Engine.Rows.Payloads;

public sealed record ErrorPayload(
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Code);
