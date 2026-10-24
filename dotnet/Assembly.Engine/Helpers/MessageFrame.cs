using System.Text.RegularExpressions;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Helpers;

/// <summary>The prompt a message between sessions arrives as, and the sending thread read back from such a prompt.</summary>
public static partial class MessageFrame
{
    /// <summary>The prompt; <paramref name="waited"/>, when it's a minute or more, says how long it waited to be delivered.</summary>
    public static string Of(string messageId, ThreadInfo from, string text, TimeSpan? waited = null) =>
        $"Message {messageId} from another agent's session (thread {from.ThreadId}, \"{from.Title}\"), " +
        "sent with the Assembly MCP server. Your answer goes back to it." +
        (waited is { TotalMinutes: >= 1 } wait ? $" It was sent {(int)wait.TotalMinutes} min ago and waited until now to be delivered." : "") +
        "\n\n" + text;

    /// <summary>The thread that sent the prompt, if it's a message from another session; null for a person's prompt.</summary>
    public static string? SenderOf(string prompt) => Sender().Match(prompt) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"^Message \S+ from another agent's session \(thread ([^,\s]+), ")]
    private static partial Regex Sender();
}
