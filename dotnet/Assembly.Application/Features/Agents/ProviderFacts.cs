using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Agents;

public enum Installation
{
    [JsonStringEnumMemberName("installed")] Installed,
    [JsonStringEnumMemberName("notInstalled")] NotInstalled,
    [JsonStringEnumMemberName("invalid")] Invalid
}
public enum AuthState
{
    [JsonStringEnumMemberName("unverified")] Unverified,
    [JsonStringEnumMemberName("signedIn")] SignedIn,
    [JsonStringEnumMemberName("needsSignIn")] NeedsSignIn,
    [JsonStringEnumMemberName("unsupported")] Unsupported,
    [JsonStringEnumMemberName("unknown")] Unknown
}
public sealed record ModelChoice(string Id, string Name);
public sealed record SignInChoice(string MethodId, string Name, string Kind);
public sealed record ProviderFacts(string Provider, string Name, string Adapter, string? Version,
    Installation Installation, AuthState Auth, ProviderProblem? Problem,
    IReadOnlyList<ModelChoice> Models, IReadOnlyList<SignInChoice> Methods, long CheckedMs);
public sealed record ProviderTarget(string Provider, string AccountContext, string Name)
{
    public string EntityId => Provider + "/" + AccountContext;
}
