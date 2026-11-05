using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Infrastructure.Updates;

/// <summary>A delivered app installing a newer version of itself.</summary>
internal interface IAppInstall
{
    /// <summary>The version that last failed to start, which only the button tries again.</summary>
    string? FailedVersion { get; }
    /// <summary>What the last update left to say, when this app is the one it went back to.</summary>
    string? Note { get; }
    /// <summary>The version downloaded, checked and waiting for the restart, if any.</summary>
    string? Ready { get; }
    /// <summary>Gets it ready and goes no further: the app never restarts by itself.</summary>
    Task StageAsync(string version, Action<string> progress, CancellationToken ct);
    /// <summary>The press: waits for running sessions, then hands over and asks the window to quit. A turn that starts meanwhile sends it back to waiting.</summary>
    Task RestartAsync(Action<string> progress, CancellationToken ct);
    /// <summary>The window is quitting: a ready update installs once it has gone, unless a turn is running. Nothing reopens it.</summary>
    Task InstallOnQuitAsync(CancellationToken ct);
    /// <summary>The window is up: an update waiting to hear that can finish.</summary>
    void Started();
    /// <summary>The launch after an install on quit: something has to put the old app back if its window never comes up.</summary>
    Task WatchFirstStartAsync(CancellationToken ct);
}

/// <param name="App">The running bundle.</param>
/// <param name="Home">This app's home, which holds the update's folder.</param>
/// <param name="Database">This app's database.</param>
/// <param name="Version">The running version.</param>
internal sealed record AppLocation(string App, string Home, string Database, string Version)
{
    internal string Folder => Path.Combine(Home, "app-update");
    internal string Failed => Path.Combine(Folder, "failed.json");
    internal string Installed => Path.Combine(Folder, AppSwap.InstalledMark);
}

/// <summary>
/// Everything before the swap. The stage sits beside the bundle so the swap stays on one volume. It's made and checked with
/// nothing stopped, and waits there: Restart to Update goes on, to the wait for sessions and the quit, and so does the next quit
/// with no turn running. The helper does the rest once this app has gone, starting with the copy of the database, so no write
/// comes after it. One helper at a time: whichever of the two starts it first.
/// </summary>
internal sealed partial class AppUpdate(AppLocation location, RemoteReleaseFeed feed, Func<CancellationToken, Task> idle, Func<bool> busy,
    Func<string, string, CancellationToken, Task<Action>> startHelper, Func<int> windowPid, Action quit, UpdateProgress? updateProgress = null) : IAppInstall
{
    private const string Bundle = "Assembly.app", StagePrefix = ".assembly-update-";
    internal const string WaitingWords = "Waiting for running sessions to finish…";
    private sealed record Staged(string Version, string Stage, string Fresh, string Program);
    private volatile Staged? _staged;
    private int _handedOver;

    /// <summary>The bundle a packaged backend runs inside; none from a checkout.</summary>
    internal static string? BundleOf(string backendFolder)
    {
        if (!File.Exists(Path.Combine(backendFolder, "packaged.json"))) return null;
        var app = Path.GetFullPath(Path.Combine(backendFolder, "../../.."));
        return app.EndsWith(".app", StringComparison.Ordinal) ? app : null;
    }

    private AppSwapResult? Read()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(location.Failed), AppSwapJson.Default.AppSwapResult); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public string? FailedVersion => Read()?.Version;
    public string? Note => Read()?.Note;

    public void Started() => AppSwap.Acknowledge(location.Folder, location.Version);

    public string? Ready => _staged?.Version;

    public async Task StageAsync(string version, Action<string> progress, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(location.App)!;
        var stage = Path.Combine(parent, $"{StagePrefix}{version}");
        var kept = ReadInstalled()?.Stage;
        if (stage == kept || HelperRunsFrom(stage)) throw new CallFailed($"Assembly {version} is still finishing an earlier update.", "app_update_failed");
        _staged = null;
        try
        {
            // What an earlier run got ready and no one restarted into: this run fetches it again.
            // Not one a helper runs from or an install on quit still needs: those hold the old app.
            foreach (var left in Directory.EnumerateDirectories(parent, StagePrefix + "*"))
                if (left != kept && !HelperRunsFrom(left)) Directory.Delete(left, recursive: true);
            progress($"Downloading Assembly {version}…");
            var release = await feed.AppReleaseAsync(version, ct);
            var unpacked = Path.Combine(stage, "new");
            Directory.CreateDirectory(unpacked);
            var archive = Path.Combine(stage, "app.tar.gz");
            await feed.DownloadAppAsync(release, archive, ct, done => updateProgress?.Download(UpdateProgress.ThisMachine, "app", done, release.Bytes));
            progress($"Checking Assembly {version}…");
            await UnpackAsync(archive, unpacked, ct);
            File.Delete(archive);
            var fresh = Path.Combine(unpacked, Bundle);
            _staged = new(version, stage, fresh, Inspect(fresh, version));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CallFailed($"Assembly can't replace itself in {parent}, so nothing was changed: {error.Message}", "app_update_failed");
        }
        finally
        {
            if (_staged is null)
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public async Task RestartAsync(Action<string> progress, CancellationToken ct)
    {
        var staged = _staged ?? throw new CallFailed("No update is ready to restart into.", "no_update");
        try
        {
            Directory.CreateDirectory(location.Folder);
            File.Delete(location.Failed);
            while (true)
            {
                progress(WaitingWords);
                await idle(ct);
                // The window is already quitting, and installs it.
                if (Interlocked.CompareExchange(ref _handedOver, 1, 0) != 0) return;
                var callOff = await HandOverAsync(staged, null, ct);
                // Starting the helper can take seconds, and a turn can start in them. Quitting now would kill it.
                if (!busy())
                {
                    progress($"Restarting into Assembly {staged.Version}…");
                    quit();
                    return;
                }
                callOff();
                Volatile.Write(ref _handedOver, 0);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CallFailed($"Assembly can't replace itself in {Path.GetDirectoryName(location.App)}, so nothing was changed: {error.Message}", "app_update_failed");
        }
    }

    public async Task InstallOnQuitAsync(CancellationToken ct)
    {
        // With the last one unconfirmed its copy is still the way back, and another install would replace it.
        if (_staged is not { } staged || busy() || File.Exists(location.Installed) || Interlocked.CompareExchange(ref _handedOver, 1, 0) != 0) return;
        Directory.CreateDirectory(location.Folder);
        File.Delete(location.Failed);
        await HandOverAsync(staged, Path.Combine(BackendFolder, BackendName), ct);
    }

    public async Task WatchFirstStartAsync(CancellationToken ct)
    {
        if (ReadInstalled() is not { } installed) return;
        // Not what was installed: someone put another version in place by hand, and it has nothing to wait for.
        if (installed.Version != location.Version)
        {
            File.Delete(location.Installed);
            return;
        }
        var planFile = Path.Combine(installed.Stage, "plan.json");
        var (window, backend) = (windowPid(), Environment.ProcessId);
        var plan = installed with { Watch = true, WindowPid = window, WindowStart = AppSwap.StartOf(window), BackendPid = backend, BackendStart = AppSwap.StartOf(backend) };
        await File.WriteAllBytesAsync(planFile, JsonSerializer.SerializeToUtf8Bytes(plan, AppSwapJson.Default.AppSwapPlan), ct);
        await startHelper(Path.Combine(installed.Stage, HelperCopy), planFile, ct);
    }

    private AppSwapPlan? ReadInstalled()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(location.Installed), AppSwapJson.Default.AppSwapPlan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // A helper holds this for as long as it runs; a folder it has left, or never ran from, opens.
    private static bool HelperRunsFrom(string stage)
    {
        try { using (new FileStream(Path.Combine(stage, AppSwap.HelperMark), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
    }

    private const string HelperCopy = "finish-update";
    private const string BackendFolder = "Contents/Resources/backend";
    private static string BackendName => Path.GetFileName(Environment.ProcessPath ?? "assembly");

    // From here the helper is waiting for this app to go; calling it off before then leaves everything as it was.
    private async Task<Action> HandOverAsync(Staged staged, string? check, CancellationToken ct)
    {
        var copy = Path.Combine(location.Folder, $"{AppSwap.BackupPrefix}{staged.Version}.db");
        var planFile = Path.Combine(staged.Stage, "plan.json");
        var plan = AppSwapPlan.For(location, staged.Fresh, staged.Program, staged.Stage, staged.Version, copy, windowPid()) with { Check = check };
        await File.WriteAllBytesAsync(planFile, JsonSerializer.SerializeToUtf8Bytes(plan, AppSwapJson.Default.AppSwapPlan), ct);
        return await startHelper(Path.Combine(staged.Fresh, BackendFolder, BackendName), planFile, ct);
    }

    // A signed older archive must not pass as this version: that would be a downgrade.
    private static string Inspect(string app, string version)
    {
        string plist;
        try { plist = File.ReadAllText(Path.Combine(app, "Contents/Info.plist")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Incomplete(); }
        var program = Executable().Match(plist).Groups[1].Value;
        if (ShortVersion().Match(plist).Groups[1].Value != version) throw new CallFailed($"The download doesn't contain Assembly {version}.", "release_invalid");
        var path = "Contents/MacOS/" + program;
        return program.Length > 0 && !program.Contains('/', StringComparison.Ordinal) && File.Exists(Path.Combine(app, path)) &&
            File.Exists(Path.Combine(app, "Contents/Resources/backend/packaged.json")) ? path : throw Incomplete();
    }

    private static CallFailed Incomplete() => new("The download isn't a complete Assembly app.", "release_invalid");

    private static async Task UnpackAsync(string archive, string folder, CancellationToken ct)
    {
        var start = new ProcessStartInfo("tar", ["-xzf", archive, "-C", folder]) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        using var process = Process.Start(start) ?? throw new CallFailed("tar could not start.", "app_update_failed");
        var said = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new CallFailed($"The download couldn't be unpacked: {(await said).Trim()}", "release_invalid");
    }

    /// <summary>
    /// Starts the helper and waits for it to say it has left this process's group. It runs from a copy in the stage: a
    /// single-file program keeps loading from its own path, and the swap moves the bundle it came from. What comes back calls
    /// it off: until this app quits it's only waiting, and nothing has been touched.
    /// </summary>
    internal static async Task<Action> StartHelperAsync(string helper, string plan, CancellationToken ct)
    {
        var copy = Path.Combine(Path.GetDirectoryName(plan)!, HelperCopy);
        if (helper != copy) File.Copy(helper, copy, overwrite: true);
        var start = new ProcessStartInfo(copy, ["finish-update", plan]) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var process = Process.Start(start) ?? throw new CallFailed("The update helper could not start.", "app_update_failed");
        void CallOff()
        {
            try { process.Kill(); process.WaitForExit(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string? said;
        try { said = await process.StandardOutput.ReadLineAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { said = null; }
        catch (OperationCanceledException)
        {
            // Past the caller's limit: it's going on without this, so nothing is left running behind it.
            CallOff();
            throw;
        }
        if (said == AppSwap.Ready) return CallOff;
        CallOff();
        throw new CallFailed("The update helper didn't start.", "app_update_failed");
    }

    [GeneratedRegex(@"<key>CFBundleExecutable</key>\s*<string>([^<]*)</string>")]
    private static partial Regex Executable();
    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]*)</string>")]
    private static partial Regex ShortVersion();
}
