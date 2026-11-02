using Assembly.Engine.Workflows.Policy;
using System.Collections.Immutable;
using Assembly.Engine.Workflows.Definitions;
namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowPolicyTests
{
    [Theory]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("env X=1 sh -c 'echo ok; gh pr merge 12'")]
    [InlineData("/usr/bin/gh --repo o/r pr ready 12")]
    [InlineData("/usr/bin/env X=1 /bin/sh -c 'gh pr merge 12'")]
    [InlineData("git -C /work push -u origin main")]
    [InlineData("git push origin +HEAD:main")]
    [InlineData("git push origin tsk:refs/heads/main")]
    [InlineData("gh workflow run release.yml")]
    [InlineData("gh api repos/o/r/dispatches")]
    [InlineData("gh api -X PUT repos/o/r/pulls/1/merge")]
    [InlineData("git push origin assembly-v1")]
    [InlineData("echo $(gh pr merge 1)")]
    [InlineData("echo \"$(gh pr merge 1)\"")]
    [InlineData("echo `gh pr ready 1`")]
    [InlineData("for n in 1 2; do gh pr merge $n; done")]
    [InlineData("gh pr \\\nmerge 1")]
    public void Fixed_deny_overrides_allow(string command) => Assert.Equal(PolicyDecision.Deny,
        new GuardPolicy(new CommandRules(["*"], [], []), PolicyDecision.Allow).Decide(command));
    [Theory]
    [InlineData("echo 'gh pr merge 12'")]
    [InlineData("grep -n \"gh pr ready\" notes.md")]
    [InlineData("git push -u origin feature")]
    [InlineData("gh api repos/o/r/pulls/1")]
    public void Quoted_text_and_safe_commands_are_not_merges(string command) => Assert.Equal(PolicyDecision.Ask, GuardPolicy.Empty.Decide(command));
    [Fact]
    public void Strictest_matching_rule_wins()
    {
        var policy = new GuardPolicy(new CommandRules(["pnpm *"], ["pnpm install*"], ["rm -rf *"]), PolicyDecision.Allow);
        Assert.Equal(PolicyDecision.Allow, policy.Decide("pnpm test"));
        Assert.Equal(PolicyDecision.Ask, policy.Decide("pnpm test && pnpm install"));
        Assert.Equal(PolicyDecision.Deny, policy.Decide("pnpm test; rm -rf /"));
        Assert.Equal(PolicyDecision.Allow, policy.Decide("ls"));
    }
    [Theory]
    [InlineData("echo 'unclosed")]
    [InlineData("echo trailing\\")]
    [InlineData("echo $(unclosed")]
    public void Unparseable_shell_asks(string command) => Assert.Equal(PolicyDecision.Ask,
        new GuardPolicy(CommandRules.Empty, PolicyDecision.Allow).Decide(command));
    [Fact]
    public void Excessive_shell_nesting_asks() => Assert.Equal(PolicyDecision.Ask,
        new GuardPolicy(CommandRules.Empty, PolicyDecision.Allow).Decide(string.Concat(Enumerable.Repeat("sh -c ", 100_000)) + "gh pr merge 1"));
}
