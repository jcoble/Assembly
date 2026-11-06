namespace Assembly.Application.Features.Agents;

public enum ProviderProblem
{
    not_installed, cannot_start, invalid_descriptor, timed_out, adapter_crashed,
    network, version_unknown, interrupted, adapter_error
}

public static class ProviderProblems
{
    public static string Text(ProviderProblem problem) => problem switch
    {
        ProviderProblem.not_installed => "The adapter is not installed.",
        ProviderProblem.cannot_start => "The adapter could not start.",
        ProviderProblem.invalid_descriptor => "The provider configuration is incomplete.",
        ProviderProblem.timed_out => "The provider did not answer in time.",
        ProviderProblem.adapter_crashed => "The adapter stopped during the check.",
        ProviderProblem.network => "The provider could not be reached.",
        ProviderProblem.version_unknown => "The adapter did not report its version.",
        ProviderProblem.interrupted => "The check was interrupted.",
        _ => "The adapter could not complete the check."
    };
}
