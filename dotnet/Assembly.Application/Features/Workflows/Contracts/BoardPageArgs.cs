namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board PageArgs contract.</summary>
public sealed record BoardPageArgs(string? Project, string? State, string? Cursor);
