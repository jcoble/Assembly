using System.Text.Json;

namespace Assembly.Application.Common.Acp.Messages;

public sealed record PermissionSubject(string Type, JsonElement? ToolCall = null, string? Command = null);
