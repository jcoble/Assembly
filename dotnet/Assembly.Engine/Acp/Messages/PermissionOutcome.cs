namespace Assembly.Engine.Acp.Messages;

/// <summary><c>{"outcome":"selected","optionId":…}</c> or <c>{"outcome":"cancelled"}</c>.</summary>
public sealed record PermissionOutcome(string Outcome, string? OptionId = null)
{
    public static PermissionOutcome Cancelled { get; } = new("cancelled");

    public static PermissionOutcome Selected(string optionId) => new("selected", optionId);
}
