using System.Text;
using System.Text.Json;

namespace Assembly.Engine.Helpers;

/// <summary>A tool's answer: its text, and whether it's an error the agent should read as one.</summary>
public sealed record ToolResult(string Text, bool IsError = false)
{
    public static ToolResult Error(string text) => new(text, IsError: true);

    /// <summary>A JSON object the body writes the properties of.</summary>
    public static ToolResult Json(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }
        return new ToolResult(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
