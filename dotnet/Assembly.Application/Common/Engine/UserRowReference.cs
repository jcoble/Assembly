namespace Assembly.Application.Common.Engine;

/// <summary>The exact user row published by an owning operation, bound to its original owning epoch.</summary>
public sealed record UserRowReference(long Seq, string RunId, long Epoch);
