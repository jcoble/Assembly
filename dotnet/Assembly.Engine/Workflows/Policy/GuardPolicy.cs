using System.Collections.Immutable;

namespace Assembly.Engine.Workflows.Policy;

public enum PolicyDecision { Allow, Ask, Deny }
public sealed record CommandRules(ImmutableArray<string> Allow, ImmutableArray<string> Ask, ImmutableArray<string> Deny)
{
    public static CommandRules Empty { get; } = new([], [], []);
}
public sealed record GuardPolicy(CommandRules Commands, PolicyDecision Default = PolicyDecision.Ask)
{
    public static GuardPolicy Empty { get; } = new(CommandRules.Empty);
    private static readonly string[] FixedDeny =
    [
        "git reset --hard*", "gh pr merge*", "gh pr ready*", "git push * main*",
        "git push *:main*", "git push *:refs/heads/main*", "gh workflow run*",
        "gh api *dispatches*", "gh api *pulls/*/merge*", "git push *assembly-*"
    ];

    public PolicyDecision Decide(string command)
    {
        var commands = ShellCommands.Parse(command);
        if (commands is null) return PolicyDecision.Ask;
        if (commands.Count == 0) return Default;
        return commands.Max(DecideOne);
    }

    private PolicyDecision DecideOne(string command)
    {
        bool Matches(IEnumerable<string> rules) => rules.Any(rule => ShellCommands.Glob(rule, command));
        if (Matches(FixedDeny) || Matches(Commands.Deny)) return PolicyDecision.Deny;
        if (Matches(Commands.Ask)) return PolicyDecision.Ask;
        if (Matches(Commands.Allow)) return PolicyDecision.Allow;
        return Default;
    }
}
