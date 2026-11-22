namespace Assembly.Application.Common.Agents;

/// <summary>
/// Provider-neutral effective context window policy resolved at the adapter boundary: an optional fixed limit,
/// plus whether later turn-end model capacity reports must never widen an already-observed window.
/// </summary>
public sealed record ContextLimitPolicy(long? FixedLimit, bool NeverWiden);
