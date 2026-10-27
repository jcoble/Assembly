namespace Assembly.Infrastructure.Clock;

/// <summary>What the clock topic carries: the backend's time now, in Unix milliseconds.</summary>
internal sealed record ClockTime(long Now);
