namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// The glyph the window draws beside a provider's sessions: the path of a Lucide icon the window bundles
/// (desktop/src/core/assets.rs). An agent it doesn't know gets a plain bot.
/// </summary>
public static class ProviderGlyph
{
    public static string Of(string agent) => agent switch
    {
        "claude" => "icons/asterisk.svg",
        "codex" => "icons/square-terminal.svg",
        "antigravity" => "icons/rocket.svg",
        _ => "icons/bot.svg",
    };
}
