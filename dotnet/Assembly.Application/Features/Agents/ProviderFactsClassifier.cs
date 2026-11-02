using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;

namespace Assembly.Application.Features.Agents;

/// <summary>Maps probe evidence to the safe provider facts contract, without retaining transport material.</summary>
public static class ProviderFactsClassifier
{
    public static ProviderFacts Classify(AgentDescriptor agent, ProviderProbeResult probe, long checkedMs)
    {
        var problem = Problem(probe.ProblemCode);
        var installation = problem switch
        {
            ProviderProblem.invalid_descriptor => Installation.Invalid,
            ProviderProblem.not_installed => Installation.NotInstalled,
            _ => Installation.Installed
        };
        // Auth status describes an identity category. Only explicit absence is negative evidence;
        // account/api_key/gateway/external and a successful session do not prove a completed login.
        var auth = probe.AuthRequired || probe.AuthStatus?.Kind == "none"
            ? AuthState.NeedsSignIn
            : probe.Session is not null ? AuthState.Unverified : AuthState.Unknown;
        var version = probe.Initialize?.Info?.Version;
        var models = probe.Session is { } session
            ? ConfigOptions.Models(session.ConfigOptions).Select(model => new ModelChoice(model.Id, model.Name)).ToArray()
            : [];
        var methods = SignInMethods.Runnable(probe.Initialize?.AuthMethods ?? [])
            .Select(SignInMethods.Present).ToArray();
        return new(agent.Id, agent.Name, probe.Initialize?.Info?.Name ?? agent.Id, version,
            installation, auth, problem ?? (version is null ? ProviderProblem.version_unknown : null),
            models, methods, checkedMs);
    }

    private static ProviderProblem? Problem(string? code) => code switch
    {
        null => null,
        "not_installed" => ProviderProblem.not_installed,
        "cannot_start" => ProviderProblem.cannot_start,
        "invalid_descriptor" => ProviderProblem.invalid_descriptor,
        "timed_out" => ProviderProblem.timed_out,
        "adapter_crashed" => ProviderProblem.adapter_crashed,
        "network" => ProviderProblem.network,
        "version_unknown" => ProviderProblem.version_unknown,
        "interrupted" => ProviderProblem.interrupted,
        _ => ProviderProblem.adapter_error
    };
}
