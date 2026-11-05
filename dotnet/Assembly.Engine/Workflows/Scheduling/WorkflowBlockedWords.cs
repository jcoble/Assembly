namespace Assembly.Engine.Workflows.Scheduling;

/// <summary>Why a step's provider or model can't run it, naming which. Other reasons a step isn't ready are the scheduler's to wait out.</summary>
internal static class WorkflowBlockedWords
{
    internal static string? Of(string? reason, DispatchEligibility w) => reason switch
    {
        "model_unavailable" => $"Model {w.Model} isn't available for {w.Provider} on this machine.",
        "sign_in_required" => $"Sign in to {w.Provider} to run this step.",
        "provider_unavailable" when w.Provider == "" => "This step names no provider.",
        "provider_unavailable" when !w.ProviderFacts.Configured => $"{w.Provider} isn't set up on this machine.",
        "provider_unavailable" when w.ProviderFacts.Installation is null or "installed" => $"{w.Provider} hasn't been checked on this machine.",
        "provider_unavailable" => $"{w.Provider} isn't installed on this machine.",
        _ => null,
    };
}
