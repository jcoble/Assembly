namespace Assembly.Engine.Threads;

/// <summary>Threads, most recently used first. <c>NextBefore</c> is the next page's <c>before</c>, or null at the end.</summary>
public sealed record ThreadPage(IReadOnlyList<ThreadInfo> Threads, long? NextBefore);
