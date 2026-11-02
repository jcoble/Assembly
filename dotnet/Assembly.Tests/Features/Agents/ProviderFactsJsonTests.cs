using System.Text.Json;
using Assembly.Application.Features.Agents;

namespace Assembly.Tests.Features.Agents;

public sealed class ProviderFactsJsonTests
{
    [Fact]
    public void Facts_use_the_exact_safe_camel_case_contract_with_real_empty_lists_and_null_version()
    {
        var facts = new ProviderFacts("codex", "Codex", "codex-acp", null, Installation.NotInstalled,
            AuthState.NeedsSignIn, ProviderProblem.not_installed, [], [], 123);
        var json = JsonSerializer.SerializeToElement(facts, ProvidersJson.Default.ProviderFacts);
        Assert.Equal(["adapter", "auth", "checkedMs", "installation", "methods", "models", "name", "problem", "provider", "version"],
            json.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("notInstalled", json.GetProperty("installation").GetString());
        Assert.Equal("needsSignIn", json.GetProperty("auth").GetString());
        Assert.Equal("not_installed", json.GetProperty("problem").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("version").ValueKind);
        Assert.Empty(json.GetProperty("models").EnumerateArray());
        Assert.Empty(json.GetProperty("methods").EnumerateArray());
        Assert.Equal(123, json.GetProperty("checkedMs").GetInt64());
        foreach (var (installation, expected) in new[] { (Installation.Installed, "installed"), (Installation.NotInstalled, "notInstalled"), (Installation.Invalid, "invalid") })
            Assert.Equal(expected, JsonSerializer.SerializeToElement(facts with { Installation = installation }, ProvidersJson.Default.ProviderFacts).GetProperty("installation").GetString());
        foreach (var (auth, expected) in new[] { (AuthState.Unverified, "unverified"), (AuthState.SignedIn, "signedIn"), (AuthState.NeedsSignIn, "needsSignIn"), (AuthState.Unsupported, "unsupported"), (AuthState.Unknown, "unknown") })
            Assert.Equal(expected, JsonSerializer.SerializeToElement(facts with { Auth = auth }, ProvidersJson.Default.ProviderFacts).GetProperty("auth").GetString());
        foreach (var (kind, expected) in new[] { (SignInKind.Agent, "agent"), (SignInKind.Terminal, "terminal") })
        {
            var choice = SignInMethods.Present(new("login", "Login", kind, [], []));
            var safeChoice = JsonSerializer.SerializeToElement(choice, ProvidersJson.Default.SignInChoice);
            Assert.Equal(expected, safeChoice.GetProperty("kind").GetString());
            Assert.Equal(["kind", "methodId", "name"], safeChoice.EnumerateObject().Select(property => property.Name).Order());
        }
    }
}
