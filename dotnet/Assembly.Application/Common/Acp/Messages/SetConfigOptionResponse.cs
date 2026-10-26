using System.Text.Json;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>Every config option with its current value, raw until a step needs it typed.</summary>
public sealed record SetConfigOptionResponse(JsonElement ConfigOptions);
