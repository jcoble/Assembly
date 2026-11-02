namespace Assembly.Engine.Sessions;

public sealed record SessionLimits(Func<int> Depth, Func<int> Children)
{
    public int ReadDepth() => Math.Max(1, Depth());
    public int ReadChildren() => Math.Clamp(Children(), 1, 10);
}
