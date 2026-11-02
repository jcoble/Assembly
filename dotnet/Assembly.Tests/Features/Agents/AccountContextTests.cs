using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;

namespace Assembly.Tests.Features.Agents;

public sealed class AccountContextTests
{
    [Fact]
    public void Account_identity_matches_exact_domain_and_strict_UTF8_vectors_without_normalization()
    {
        var vectors = new[]
        {
            ("a", "acct-v1:1bdf2899d1086556ad66a2ba1173d3ad740d87c7353d09d4eca2f292edabfe55"),
            ("é", "acct-v1:9862d155d23ae8d08e5e32961fdec4dbcb6d42d04cf281a25e6e84e8e091c57c"),
            ("e\u0301", "acct-v1:b33f48e49554789a46fa7d028ca4f9b76f657521772053fe3583a36a868e1c3c"),
            ("a\0b", "acct-v1:e319c44da405e5df68a5b9c31fda09fd06ec50b46cff9863cb98b7b54d2363ae"),
            ("acct-v1:" + new string('0', 64), "acct-v1:f7c52180ffb0d7c3c94ce3a83f2ee1565d44b140e2183c0f14111f5f371e3866")
        };
        foreach (var (raw, expected) in vectors)
        {
            var descriptor = Agent("codex", new Dictionary<string, string> { ["CODEX_HOME"] = raw });
            Assert.True(AccountContext.Of(descriptor, _ => null) == expected, "Account identity does not match the reviewed exact-byte vector.");
        }
    }

    [Fact]
    public void Explicit_and_inherited_home_use_one_identity_and_explicit_empty_wins()
    {
        const string home = "/isolated-profile";
        const string expected = "acct-v1:bba8f0244af078a391d9faaebbd7df99750b21889fda2190393b2863a58c6e5b";
        foreach (var (family, variable) in new[] { ("codex", "CODEX_HOME"), ("claude", "CLAUDE_CONFIG_DIR") })
        {
            var inherited = new Dictionary<string, string> { [variable] = home };
            Assert.True(AccountContext.Of(Agent(family), key => inherited.GetValueOrDefault(key)) == expected);
            var reads = 0;
            Assert.True(AccountContext.Of(Agent(family, new Dictionary<string, string> { [variable] = home }), _ => { reads++; return "/different"; }) == expected);
            Assert.Equal("", AccountContext.Of(Agent(family, new Dictionary<string, string> { [variable] = "" }), _ => { reads++; return home; }));
            Assert.Equal(0, reads);
            Assert.Equal("", AccountContext.Of(Agent(family), _ => null));
            Assert.Equal("", AccountContext.Of(Agent(family), _ => ""));
        }
        Assert.Equal("", AccountContext.Of(Agent("another-family"), _ => throw new InvalidOperationException("must not inspect environment")));
    }

    [Fact]
    public void Captured_environment_account_is_stable_after_ambient_and_unrelated_descriptor_changes()
    {
        var ambient = new Dictionary<string, string> { ["CODEX_HOME"] = "/isolated-profile", ["UNRELATED"] = "before" };
        var captured = new Dictionary<string, string>(ambient);
        var original = Agent("codex");
        var account = AccountContext.Of(original, key => captured.GetValueOrDefault(key));
        var configuration = ConfigTargetIdentity.Of(original, captured);
        ambient["CODEX_HOME"] = "/replacement-profile";
        ambient["UNRELATED"] = "after";
        var updated = original with { Id = "different-configured-provider", Command = "updated-adapter", Args = ["different-argument"] };
        Assert.Equal(account, AccountContext.Of(updated, key => captured.GetValueOrDefault(key)));
        Assert.NotEqual(configuration, ConfigTargetIdentity.Of(updated, captured));
        Assert.NotEqual(account, AccountContext.Of(original, key => ambient.GetValueOrDefault(key)));
        Assert.StartsWith("acct-v1:", account, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_UTF16_refuses_with_a_fixed_safe_error()
    {
        foreach (var raw in new[] { "private-invalid-canary\ud800", "\udc00private-invalid-canary" })
        {
            var failure = Assert.Throws<CallFailed>(() => AccountContext.Of(Agent("codex", new Dictionary<string, string> { ["CODEX_HOME"] = raw }), _ => null));
            Assert.Equal("invalid account context", failure.Message);
            Assert.Equal("invalid_account_context", failure.Reason);
        }
    }

    [Fact]
    public void Syntax_guard_accepts_only_empty_or_exact_lowercase_opaque_identity()
    {
        Assert.True(AccountContext.IsValid(""));
        Assert.True(AccountContext.IsValid("acct-v1:" + new string('a', 64)));
        Assert.True(AccountContext.IsValid("acct-v1:" + new string('0', 64)));
        foreach (var invalid in new string?[] { null, "/private-home", "acct-v1:", "acct-v1:" + new string('a', 63),
            "acct-v1:" + new string('a', 65), "acct-v1:" + new string('A', 64), "acct-v1:" + new string('g', 64),
            "ACCT-v1:" + new string('a', 64), "acct-v2:" + new string('a', 64), " acct-v1:" + new string('a', 64) })
            Assert.False(AccountContext.IsValid(invalid));
    }

    private static AgentDescriptor Agent(string family, IReadOnlyDictionary<string, string>? environment = null) =>
        new("configured-provider", "Provider", "adapter", [], environment, VendorFamily: family);
}
