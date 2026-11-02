namespace Assembly.Engine.Messages;

public static class MessageText
{
    public static string Prompt(string label, string body)
    {
        var quoted = body.Replace("\n[message from ", "\n> [message from ", StringComparison.Ordinal);
        if (quoted.StartsWith("[message from ", StringComparison.Ordinal)) quoted = "> " + quoted;
        return $"[message from {label.ReplaceLineEndings(" ")}]\n{quoted}";
    }

    public static string Label(string? title, string? sessionId, string senderKind) => senderKind switch
    {
        "window" => "Jesse",
        "system" => "Assembly",
        _ => $"{title} ({sessionId})"
    };
}
