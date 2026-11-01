namespace Assembly.Engine.Messages;

public static class MessageText
{
    public static string Label(string? title, string? sessionId, string senderKind) => senderKind switch
    {
        "window" => "Jesse",
        "system" => "Assembly",
        _ => $"{title} ({sessionId})"
    };
}
