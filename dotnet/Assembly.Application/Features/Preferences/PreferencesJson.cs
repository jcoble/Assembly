using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Preferences;

/// <summary>The preferences' JSON: a preference on its topic, and a saved value as the page sent it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Preference))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class PreferencesJson : JsonSerializerContext;
