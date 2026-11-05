namespace Assembly.Infrastructure.Persistence;

/// <summary>
/// One backend per home folder: each host holds an exclusive lock on <c>home/backend.lock</c> (flock on Unix) from before
/// its database opens until it stops. A second backend on that home stops at once, before it writes anything, so its
/// recovery never closes the live one's running turns. The holder's pid is in <c>home/backend.lock.pid</c>: on Unix
/// every FileStream open of the locked file itself takes a flock, so the second backend couldn't read it there.
/// The update helper also names itself in <c>home/backend.lock.update</c>; it counts only while it names the holder.
/// </summary>
public static class HomeLock
{
    public static FileStream Take(string home, bool update = false)
    {
        var path = Path.Combine(home, "backend.lock");
        var pidPath = path + ".pid";
        var updatePath = path + ".update";
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) when (File.Exists(pidPath))
        {
            var holder = File.ReadAllText(pidPath).Trim();
            throw new HomeInUseException($"home in use by pid {holder}", File.Exists(updatePath) && File.ReadAllText(updatePath).Trim() == holder);
        }

        File.WriteAllText(pidPath, $"{Environment.ProcessId}\n");
        if (update) File.WriteAllText(updatePath, $"{Environment.ProcessId}\n");
        else File.Delete(updatePath);
        return file;
    }
}

/// <summary>Another process holds this home folder.</summary>
/// <param name="update">The update helper holds it, so it frees in a moment.</param>
public sealed class HomeInUseException(string message, bool update = false) : InvalidOperationException(message)
{
    public bool Update { get; } = update;
}
