using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Socket;

/// <summary>A frame from the page, of any type: <c>t</c> says which, and which of the others it carries (plan 5.1).</summary>
internal sealed record InFrame(string? T, string? Token, int? Protocol, long? Id, string? Feature, string? Method, JsonElement Args);

internal sealed record WelcomeFrame(int Protocol, string Machine)
{
    [JsonPropertyOrder(-1)]
    public string T => "welcome";
}

internal sealed record ResultFrame(long Id, JsonElement Result)
{
    [JsonPropertyOrder(-1)]
    public string T => "result";
}

/// <summary><c>code</c> is <c>not_found</c>, <c>bad_args</c>, <c>failed</c>, <c>cancelled</c> or <c>timeout</c>.</summary>
internal sealed record ErrorFrame(long Id, string Code, string Message)
{
    [JsonPropertyOrder(-1)]
    public string T => "error";
}
