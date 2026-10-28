namespace Assembly.Application.Common.Agents;

/// <summary>The small, quick model the backend asks for its own work, such as naming a new session.</summary>
public interface IUtilityModel
{
    /// <summary>The model's answer to <paramref name="prompt"/>; null when none is set up or its run failed.</summary>
    Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken);
}
