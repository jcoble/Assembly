namespace Assembly.Application.Features.Diagnostics;

/// <summary><c>soak.append</c>'s args: one CSV line for soak-results/<c>name</c>.</summary>
internal sealed record SoakLine(string Name, string Line);
