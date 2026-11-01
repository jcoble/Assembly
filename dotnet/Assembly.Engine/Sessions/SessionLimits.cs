namespace Assembly.Engine.Sessions;

public sealed record SessionLimits(Func<int> Depth, Func<int> Children);
