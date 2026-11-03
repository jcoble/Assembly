using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Calls;

/// <summary>The exact Engine source selected for a read. Zero is the local route marker, not a native witness.</summary>
public sealed record ExpectedSource(string Machine, long Generation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RouteMachine);
