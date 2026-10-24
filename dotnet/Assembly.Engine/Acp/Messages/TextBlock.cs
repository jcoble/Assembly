namespace Assembly.Engine.Acp.Messages;

/// <summary>A text content block, the one kind every agent must accept in a prompt.</summary>
public sealed record TextBlock(string Type, string Text)
{
    public static TextBlock Of(string text) => new("text", text);
}
