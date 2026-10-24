using System.Text.Json;

namespace Assembly.Engine.Acp.Messages;

/// <summary>The resumed session's config options, raw. Its modes come back too; nothing reads them.</summary>
public sealed record ResumeSessionResponse(JsonElement ConfigOptions);
