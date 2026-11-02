namespace Assembly.Application.Common.Agents;

public static class AccountContext
{
    public static string Of(AgentDescriptor agent, Func<string, string?> inheritedEnvironment)
    {
        var variable = agent.VendorFamily switch { "codex" => "CODEX_HOME", "claude" => "CLAUDE_CONFIG_DIR", _ => null };
        if (variable is null) return "";
        return agent.Env?.TryGetValue(variable, out var explicitValue) == true
            ? explicitValue : inheritedEnvironment(variable) ?? "";
    }
}
