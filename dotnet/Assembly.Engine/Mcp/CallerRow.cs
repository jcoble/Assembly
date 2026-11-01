namespace Assembly.Engine.Mcp;

public sealed record CallerRow(string SessionId, long Epoch, string State, string? ParentId, long Depth,
    string? RunId, string? StepId, int? Attempt, string Provider, string Model, string Folder, string Title);
