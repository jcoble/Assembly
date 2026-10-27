namespace Assembly.Infrastructure.Persistence;

/// <summary>One backend per home folder.</summary>
public static class HomeLock
{
    public static FileStream Take(string home) => throw new NotImplementedException();
}
