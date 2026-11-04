namespace Assembly.Application.Common.Agents;

public enum UtilityStatus { Succeeded, Unavailable, Failed }
public sealed record UtilityResult(UtilityStatus Status, string? Text, string? Reason);

/// <summary>One disposable configured helper request, with an explicit outcome.</summary>
public interface IUtilityModel
{
    Task<UtilityResult> CompleteAsync(string prompt, CancellationToken cancellationToken);
}
