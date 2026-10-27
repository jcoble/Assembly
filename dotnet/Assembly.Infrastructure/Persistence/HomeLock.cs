namespace Assembly.Infrastructure.Persistence;

/// <summary>
/// One backend per home folder: each host holds an exclusive lock on <c>home/backend.lock</c> (flock on Unix) from before
/// its database opens until it stops. A second backend on that home stops at once, before it writes anything, so its
/// recovery never closes the live one's running turns. The holder's pid is in <c>home/backend.lock.pid</c>: on Unix
/// every FileStream open of the locked file itself takes a flock, so the second backend couldn't read it there.
/// </summary>
public static class HomeLock
{
    public static FileStream Take(string home)
    {
        var path = Path.Combine(home, "backend.lock");
        var pidPath = path + ".pid";
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) when (File.Exists(pidPath))
        {
            throw new InvalidOperationException($"home in use by pid {File.ReadAllText(pidPath).Trim()}");
        }

        File.WriteAllText(pidPath, $"{Environment.ProcessId}\n");
        return file;
    }
}
