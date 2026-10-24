namespace Assembly.Engine.Helpers;

/// <summary>
/// Limits on helper sessions: how many a session's agent may have at work at once, and how many messages one session
/// may send any other (each way). A helper can't start helpers of its own.
/// </summary>
public sealed record HelperLimits(int UnfinishedHelpers = 5, int MessagesPerPair = 20);
