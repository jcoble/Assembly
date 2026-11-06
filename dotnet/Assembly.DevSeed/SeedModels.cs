using System.Text.Json;

namespace Assembly.DevSeed;

public static class SeedMode
{
    public const string Reset = "reset";
    public const string IfEmpty = "if-empty";
}

public sealed record SeedRequest(string CanonicalWorktree, string ComputedHome, string Mode, long NowMs);
public sealed record SeedResult(string Action, int Sessions = 0, int Projects = 0, int Runs = 0);

public static class SeedJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Stringify<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Expected non-null JSON.");
}
