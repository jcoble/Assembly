using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// What the helper needs, written by the old version and read by the new one. <paramref name="Check"/> is set for an install
/// on quit: the program in the new bundle that proves it starts, run in place of opening the window, which stays closed.
/// <paramref name="Watch"/> is set on the launch after that one: the bundles are already swapped, and the helper waits to hear
/// the window came up.
/// </summary>
internal sealed record AppSwapPlan(string App, string Fresh, string Program, string Stage, string Folder, string Version, string Previous,
    string Database, string Backup, int WindowPid, long WindowStart, int BackendPid, long BackendStart, int QuitSeconds, int AcknowledgeSeconds,
    string? Check = null, bool Watch = false)
{
    internal static AppSwapPlan For(AppLocation location, string fresh, string program, string stage, string version, string backup, int window)
    {
        var backend = Environment.ProcessId;
        return new(location.App, fresh, program, stage, location.Folder, version, location.Version, location.Database, backup,
            window, AppSwap.StartOf(window), backend, AppSwap.StartOf(backend), 300, 120);
    }
}

/// <summary>Why an update didn't happen, for the app it went back to.</summary>
internal sealed record AppSwapResult(string Version, string Note);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSwapPlan))]
[JsonSerializable(typeof(AppSwapResult))]
internal sealed partial class AppSwapJson : JsonSerializerContext;

/// <summary>
/// The helper, run from the new bundle after leaving the old app's process group. It waits for exactly the old window and
/// backend to exit, copies the database they left, swaps the bundles in one rename, starts the new one, and waits to hear from
/// it. After a quit it doesn't open the new one: it runs the new version's check, which opens the database, and leaves the app
/// closed either way, with the old bundle and the copy kept: a database that opens doesn't prove a window does, so only the next
/// launch saying it's up lets them go. A new version that exits or stays silent is stopped, by its own process id only, and the
/// old bundle and database go back.
/// It holds the home lock whenever nothing else should have the database, so a reopened backend stops with home in use.
/// Only the latest copy and the latest failed database are kept.
/// </summary>
internal static partial class AppSwap
{
    internal const string Ready = "ready";
    internal const string BackupPrefix = "assembly-before-";
    /// <summary>Beside the plan, held for as long as a helper runs from that folder, so staging can tell.</summary>
    internal const string HelperMark = "helper.lock";
    /// <summary>In the update's folder from an install on quit until its window is up: the plan that can still undo it.</summary>
    internal const string InstalledMark = "installed.json";
    // The -shm file is only an index of the log, and SQLite rebuilds it on the first open, so it's never copied.
    private static readonly string[] KeptFiles = ["-wal", ""];
    private const string Socket = "ack.sock";
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    internal static async Task<int> MainAsync(string planFile)
    {
        // The launcher kills the backend's whole group when the backend exits. Say so only once out of it.
        _ = SetSession();
        var plan = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(planFile), AppSwapJson.Default.AppSwapPlan)!;
        await Console.Out.WriteLineAsync(Ready);
        await Console.Out.FlushAsync();
        return await RunAsync(plan);
    }

    internal static long StartOf(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.StartTime.ToUniversalTime().Ticks;
    }

    internal static async Task<int> RunAsync(AppSwapPlan plan)
    {
        using var running = new FileStream(Path.Combine(plan.Stage, HelperMark), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return plan.Watch ? await WatchAsync(plan) : await InstallAsync(plan);
    }

    private static async Task<int> InstallAsync(AppSwapPlan plan)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(plan.QuitSeconds));
        try
        {
            await ExitOf(plan.WindowPid, plan.WindowStart, limit.Token);
            await ExitOf(plan.BackendPid, plan.BackendStart, limit.Token);
        }
        catch (OperationCanceledException)
        {
            Say(plan, $"Assembly {plan.Previous} didn't quit, so {plan.Version} wasn't installed.");
            Remove(plan.Stage);
            return 1;
        }
        Directory.CreateDirectory(plan.Folder);
        // Opened again already: that app has the files, and installs at its own quit. Nothing here has touched them.
        var home = await TakeHomeAsync(plan);
        if (home is null)
        {
            Remove(plan.Stage);
            return 1;
        }
        try { return await SwapAsync(plan, home); }
        finally { await home.DisposeAsync(); }
    }

    private static async Task<int> SwapAsync(AppSwapPlan plan, FileStream home)
    {
        var address = Path.Combine(plan.Folder, Socket);
        File.Delete(address);
        using var listener = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(address));
        listener.Listen(1);
        try
        {
            CopyDatabase(plan);
            Exchange(plan.Fresh, plan.App);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Say(plan, $"Assembly {plan.Version} couldn't be put in place, so this app stayed on {plan.Previous}.");
            File.Delete(address);
            await home.DisposeAsync();
            Relaunch(plan);
            return 1;
        }
        var swapped = DateTime.UtcNow;
        Process? child = null;
        try
        {
            // A window's backend takes the home itself, so it's free from here until this has to go back.
            if (plan.Check is null) await home.DisposeAsync();
            child = plan.Check is null ? Launch(plan) : Check(plan);
            if (plan.Check is null ? await AcknowledgedAsync(listener, child, plan) : await CheckedAsync(child, plan))
            {
                File.Delete(address);
                if (plan.Check is null) Remove(plan.Stage);
                else WriteMark(plan);
                return 0;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (child is not null) await StopAsync(child);
        await StopBackendAsync(plan, swapped);
        File.Delete(address);
        var held = plan.Check is null ? await TakeHomeAsync(plan) : home;
        if (held is null)
        {
            Say(plan, OpenAgain(plan));
            return 1;
        }
        await using (held) return await WentBackAsync(plan, held);
    }

    private static string OpenAgain(AppSwapPlan plan) =>
        $"Assembly {plan.Version} didn't start, and {plan.Previous} couldn't be put back because Assembly is open again. It's kept in {plan.Fresh}.";

    private static async Task<int> WentBackAsync(AppSwapPlan plan, FileStream home)
    {
        // A window may have been used before this went back: what it wrote stays in the kept database, and the note says where.
        var written = plan.Check is null && Changed(plan);
        if (await GoBackAsync(plan) is { } stuck)
        {
            // The stage may hold the old app now, so it stays, and whatever is in place opens with the reason on it.
            Say(plan, stuck);
            await home.DisposeAsync();
            if (plan.Check is not null) return 1;
            try { using (Launch(plan)) { } }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return 1;
        }
        var kept = written ? $" Anything written after the update is kept in {plan.Database}.failed-{plan.Version}, not in this app's database." : "";
        Say(plan, $"Assembly {plan.Version} didn't start, so this app went back to {plan.Previous}.{kept}");
        await home.DisposeAsync();
        Relaunch(plan);
        return 1;
    }

    // The launch after an install on quit: the window saying it's up, which removes the mark, is the only thing that lets the old
    // app go. Its backend leaving first, or nothing in time, and the old one comes back with the reason on it.
    private static async Task<int> WatchAsync(AppSwapPlan plan)
    {
        var mark = Path.Combine(plan.Folder, InstalledMark);
        var address = Path.Combine(plan.Folder, Socket);
        File.Delete(address);
        using (var listener = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(address));
            listener.Listen(1);
            // Bound before this look, so a window that comes up after it is heard.
            if (File.Exists(mark))
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(plan.AcknowledgeSeconds));
                try { await await Task.WhenAny(HearAsync(listener, plan.Version, limit.Token), ExitOf(plan.BackendPid, plan.BackendStart, limit.Token)); }
                catch (OperationCanceledException) { }
            }
        }
        File.Delete(address);
        if (!File.Exists(mark))
        {
            foreach (var suffix in KeptFiles) File.Delete(plan.Backup + suffix);
            Remove(plan.Stage);
            return 0;
        }
        await StopSameAsync(plan.WindowPid, plan.WindowStart);
        await StopSameAsync(plan.BackendPid, plan.BackendStart);
        var home = await TakeHomeAsync(plan);
        if (home is null)
        {
            Say(plan, OpenAgain(plan));
            return 1;
        }
        File.Delete(mark);
        await using (home) return await WentBackAsync(plan, home);
    }

    private static void WriteMark(AppSwapPlan plan)
    {
        var mark = Path.Combine(plan.Folder, InstalledMark);
        File.WriteAllBytes(mark + ".partial", JsonSerializer.SerializeToUtf8Bytes(plan with { Check = null }, AppSwapJson.Default.AppSwapPlan));
        File.Move(mark + ".partial", mark, overwrite: true);
    }

    // The lock frees as the holder exits, so a moment's wait tells that apart from a backend really holding it.
    private static async Task<FileStream?> TakeHomeAsync(AppSwapPlan plan)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return HomeLock.Take(Path.GetDirectoryName(plan.Database)!); }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                if (attempt == SwapAttempts) return null;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task StopSameAsync(int pid, long start)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - start) <= StartTolerance.Ticks) await StopAsync(process);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
    }

    // Null when all of it went back; otherwise what's still wrong.
    private static async Task<string?> GoBackAsync(AppSwapPlan plan)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Exchange(plan.Fresh, plan.App); break; }
            catch (IOException) when (attempt < SwapAttempts) { await Task.Delay(TimeSpan.FromSeconds(1)); }
            catch (IOException) { return $"Assembly {plan.Version} didn't start, and {plan.Previous} couldn't be put back. It's kept in {plan.Fresh}."; }
        }
        try { Restore(plan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        { return $"Assembly {plan.Version} didn't start. This app went back to {plan.Previous}, but its database couldn't be put back. The copy is {plan.Backup}."; }
        return null;
    }

    // Every process that wrote it has gone, so its files are the whole of it, right up to the last write.
    private static void CopyDatabase(AppSwapPlan plan)
    {
        foreach (var old in Directory.EnumerateFiles(plan.Folder, BackupPrefix + "*")) File.Delete(old);
        // The database itself comes last and by rename: it being there says the whole copy is, and only then can it go back.
        foreach (var suffix in KeptFiles)
        {
            if (!File.Exists(plan.Database + suffix)) continue;
            var partial = plan.Backup + suffix + ".partial";
            File.Copy(plan.Database + suffix, partial, overwrite: true);
            File.Move(partial, plan.Backup + suffix);
        }
    }

    // After a quit the app stays closed, whatever happened.
    private static void Relaunch(AppSwapPlan plan)
    {
        if (plan.Check is not null) { Remove(plan.Stage); return; }
        try { using (Launch(plan)) { } }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        Remove(plan.Stage);
    }

    private static async Task ExitOf(int pid, long start, CancellationToken ct)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            try { if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - start) > StartTolerance.Ticks) return; }
            catch (InvalidOperationException) { return; }
            await process.WaitForExitAsync(ct);
        }
    }

    // Its output goes nowhere: the window would otherwise write to this helper's pipe after it had gone. exec keeps the id.
    private static Process Launch(AppSwapPlan plan)
    {
        var program = Path.Combine(plan.App, plan.Program);
        var start = new ProcessStartInfo("/bin/sh", ["-c", "exec \"$0\" </dev/null >/dev/null 2>&1", program]) { UseShellExecute = false };
        return Process.Start(start) ?? throw new InvalidOperationException("The app could not start.");
    }

    private static Process Check(AppSwapPlan plan)
    {
        var program = Path.Combine(plan.App, plan.Check!);
        var start = new ProcessStartInfo("/bin/sh", ["-c", "exec \"$0\" check-start \"$1\" </dev/null >/dev/null 2>&1", program, plan.Database]) { UseShellExecute = false };
        return Process.Start(start) ?? throw new InvalidOperationException("The new version could not start.");
    }

    private static async Task<bool> CheckedAsync(Process check, AppSwapPlan plan)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(plan.AcknowledgeSeconds));
        try { await check.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { return false; }
        return check.ExitCode == 0;
    }

    private static async Task<bool> AcknowledgedAsync(System.Net.Sockets.Socket listener, Process child, AppSwapPlan plan)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(plan.AcknowledgeSeconds));
        var exited = child.WaitForExitAsync(limit.Token);
        var heard = HearAsync(listener, plan.Version, limit.Token);
        var first = await Task.WhenAny(heard, exited);
        if (first != heard) return false;
        try { return await heard; }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task<bool> HearAsync(System.Net.Sockets.Socket listener, string version, CancellationToken ct)
    {
        while (true)
        {
            using var caller = await listener.AcceptAsync(ct);
            var buffer = new byte[64];
            var count = await caller.ReceiveAsync(buffer, SocketFlags.None, ct);
            if (Encoding.UTF8.GetString(buffer, 0, count) == version) return true;
        }
    }

    /// <summary>The new version's backend says its window is up. With no update waiting there's nobody to tell.</summary>
    internal static void Acknowledge(string folder, string version)
    {
        var mark = Path.Combine(folder, InstalledMark);
        if (File.Exists(mark)) File.Delete(mark);
        var address = Path.Combine(folder, Socket);
        if (!File.Exists(address)) return;
        try
        {
            using var caller = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            caller.Connect(new UnixDomainSocketEndPoint(address));
            caller.Send(Encoding.UTF8.GetBytes(version));
        }
        catch (SocketException) { }
    }

    private static async Task StopAsync(Process child)
    {
        if (child.HasExited) return;
        _ = Signal(child.Id, Terminate);
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await child.WaitForExitAsync(grace.Token); }
        catch (OperationCanceledException)
        {
            _ = Signal(child.Id, ForceKill);
            await child.WaitForExitAsync(CancellationToken.None);
        }
    }

    // A window that had to be force-killed can't stop its backend. The id comes from the lock this run's backend wrote.
    private static async Task StopBackendAsync(AppSwapPlan plan, DateTime swapped)
    {
        var file = Path.Combine(Path.GetDirectoryName(plan.Database)!, "backend.lock.pid");
        try
        {
            // This helper wrote it too, when it took the lock, and a backend that never started won't have replaced that.
            if (File.GetLastWriteTimeUtc(file) < swapped - StartTolerance || !int.TryParse((await File.ReadAllTextAsync(file)).Trim(), CultureInfo.InvariantCulture, out var pid) || pid == Environment.ProcessId) return;
            using var backend = Process.GetProcessById(pid);
            await StopAsync(backend);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    // A migrated database can't be read by the older version. What's there is kept beside it, not deleted, and the backup goes in
    // whole as one file, by one rename: a stop at any moment leaves a database at the path.
    private static void Restore(AppSwapPlan plan)
    {
        if (!File.Exists(plan.Backup)) return;
        var whole = plan.Database + ".restoring";
        foreach (var suffix in KeptFiles)
            if (File.Exists(plan.Backup + suffix)) File.Copy(plan.Backup + suffix, whole + suffix, overwrite: true);
        if (File.Exists(whole + "-wal")) Fold(whole);
        var name = Path.GetFileName(plan.Database);
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(plan.Database)!, name + "*.failed-*")) File.Delete(old);
        if (File.Exists(plan.Database)) File.Copy(plan.Database, $"{plan.Database}.failed-{plan.Version}", overwrite: true);
        // A log can't stay beside a database it isn't from. With it gone what's at the path is still whole, as of its last checkpoint.
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(plan.Database + suffix)) File.Move(plan.Database + suffix, $"{plan.Database}{suffix}.failed-{plan.Version}", overwrite: true);
        File.Move(whole, plan.Database, overwrite: true);
    }

    // Whether the database is no longer what was copied before the update, byte for byte, log included.
    private static bool Changed(AppSwapPlan plan) =>
        File.Exists(plan.Backup) && KeptFiles.Any(suffix => !SameBytes(plan.Database + suffix, plan.Backup + suffix));

    private static bool SameBytes(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second)) return File.Exists(first) == File.Exists(second);
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        if (a.Length != b.Length) return false;
        var (left, right) = (new byte[1 << 16], new byte[1 << 16]);
        for (int read; (read = a.Read(left)) > 0;)
        {
            b.ReadExactly(right.AsSpan(0, read));
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read))) return false;
        }
        return true;
    }

    // Writes what's only in the log into the file itself, on the copy and with nothing else open, so one rename can place all of it.
    private static void Fold(string file)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            db.Open();
            using var checkpoint = db.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }
        foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(file + suffix);
    }

    private static void Say(AppSwapPlan plan, string note)
    {
        Directory.CreateDirectory(plan.Folder);
        var file = Path.Combine(plan.Folder, "failed.json");
        var partial = file + ".partial";
        File.WriteAllBytes(partial, JsonSerializer.SerializeToUtf8Bytes(new AppSwapResult(plan.Version, note), AppSwapJson.Default.AppSwapResult));
        File.Move(partial, file, overwrite: true);
    }

    private static void Remove(string stage)
    {
        try { Directory.Delete(stage, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Swaps two paths in one step: there's no moment without an app at either.</summary>
    internal static void Exchange(string first, string second)
    {
        var failed = OperatingSystem.IsMacOS()
            ? SwapMac(AtWorkingFolderMac, first, AtWorkingFolderMac, second, ExchangeFlag)
            : SwapLinux(AtWorkingFolderLinux, first, AtWorkingFolderLinux, second, ExchangeFlag);
        if (failed != 0) throw new IOException($"Could not swap {first} and {second}: error {Marshal.GetLastPInvokeError()}");
    }

    private const int AtWorkingFolderLinux = -100, AtWorkingFolderMac = -2;
    private const uint ExchangeFlag = 2;
    private const int SwapAttempts = 3;
    private const int Terminate = 15, ForceKill = 9;

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int SwapMac(int fromFolder, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toFolder, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int SwapLinux(int fromFolder, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toFolder, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
    [DllImport("libc", EntryPoint = "setsid")]
    private static extern int SetSession();
    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Signal(int pid, int signal);
    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int ParentOf();

    /// <summary>The window started this backend, so it's the parent.</summary>
    internal static int Window() => ParentOf();
}
