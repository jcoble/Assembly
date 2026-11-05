using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Updates;

/// <summary>What the helper needs, written by the old version and read by the new one.</summary>
internal sealed record AppSwapPlan(string App, string Fresh, string Program, string Stage, string Folder, string Version, string Previous,
    string Database, string Backup, int WindowPid, long WindowStart, int BackendPid, long BackendStart, int QuitSeconds, int AcknowledgeSeconds)
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
/// it. A new version that exits or stays silent is stopped, by its own process id only, and the old bundle and database go back.
/// Only the latest copy and the latest failed database are kept.
/// </summary>
internal static partial class AppSwap
{
    internal const string Ready = "ready";
    internal const string BackupPrefix = "assembly-before-";
    // The -shm file is only an index of the log, and SQLite rebuilds it on the first open, so it's moved aside but never copied.
    private static readonly string[] DatabaseFiles = ["", "-wal", "-shm"];
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
            Relaunch(plan);
            return 1;
        }
        var swapped = DateTime.UtcNow;
        Process? child = null;
        try
        {
            child = Launch(plan);
            if (await AcknowledgedAsync(listener, child, plan))
            {
                File.Delete(address);
                Remove(plan.Stage);
                return 0;
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (child is not null) await StopAsync(child);
        await StopBackendAsync(plan, swapped);
        if (await GoBackAsync(plan) is { } stuck)
        {
            // The stage may hold the old app now, so it stays, and whatever is in place opens with the reason on it.
            Say(plan, stuck);
            File.Delete(address);
            try { using (Launch(plan)) { } }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return 1;
        }
        Say(plan, $"Assembly {plan.Version} didn't start, so this app went back to {plan.Previous}.");
        File.Delete(address);
        Relaunch(plan);
        return 1;
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
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

    private static void Relaunch(AppSwapPlan plan)
    {
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
            if (File.GetLastWriteTimeUtc(file) < swapped - StartTolerance || !int.TryParse((await File.ReadAllTextAsync(file)).Trim(), CultureInfo.InvariantCulture, out var pid)) return;
            using var backend = Process.GetProcessById(pid);
            await StopAsync(backend);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    // A migrated database can't be read by the older version. What's there is kept beside it, not deleted.
    private static void Restore(AppSwapPlan plan)
    {
        if (!File.Exists(plan.Backup)) return;
        var name = Path.GetFileName(plan.Database);
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(plan.Database)!, name + "*.failed-*")) File.Delete(old);
        foreach (var suffix in DatabaseFiles)
            if (File.Exists(plan.Database + suffix)) File.Move(plan.Database + suffix, $"{plan.Database}{suffix}.failed-{plan.Version}", overwrite: true);
        foreach (var suffix in KeptFiles)
        {
            if (!File.Exists(plan.Backup + suffix)) continue;
            var partial = plan.Database + suffix + ".partial";
            File.Copy(plan.Backup + suffix, partial, overwrite: true);
            File.Move(partial, plan.Database + suffix);
        }
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
