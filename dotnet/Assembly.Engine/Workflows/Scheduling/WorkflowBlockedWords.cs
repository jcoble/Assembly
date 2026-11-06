namespace Assembly.Engine.Workflows.Scheduling;

/// <summary>Why a step's provider or model can't run it, naming which. Other reasons a step isn't ready, such as a check still running, are the scheduler's to wait out.</summary>
internal static class WorkflowBlockedWords
{
    /// <summary>A step's dispatch state after Retry on one of these: what stopped it may have changed, so its provider is looked at again first.</summary>
    internal const string Recheck = "provider_recheck";

    internal static bool StoppedByProvider(string? reason) => reason is "model_unavailable" or "sign_in_required" or "provider_check_failed" or "provider_unavailable";

    internal static string? Of(string? reason, DispatchEligibility w) => reason switch
    {
        "model_unavailable" => $"Model {w.Model} isn't available for {w.Provider} on this machine.",
        "sign_in_required" => $"Sign in to {w.Provider} to run this step.",
        "provider_check_failed" when w.ProviderFacts.Installation is null or "unknown" => $"Couldn't tell whether {w.Provider} is installed on this machine.",
        "provider_check_failed" => $"Couldn't check whether {w.Provider} is signed in on this machine.",
        "provider_unavailable" when w.Provider == "" => "This step names no provider.",
        "provider_unavailable" when !w.ProviderFacts.Configured => $"{w.Provider} isn't set up on this machine.",
        "provider_unavailable" when w.ProviderFacts.Installation == "installed" => $"{w.Provider} didn't say what it can run on this machine.",
        "provider_unavailable" => $"{w.Provider} isn't installed on this machine.",
        _ => null,
    };
}
