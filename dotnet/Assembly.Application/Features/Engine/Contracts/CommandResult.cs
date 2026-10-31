namespace Assembly.Application.Features.Engine.Contracts;

public sealed record CommandResult(bool Applied, bool Late, string? Error,
    long Version, long Seq, long Epoch);
