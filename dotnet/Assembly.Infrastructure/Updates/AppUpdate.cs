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
    /// <summary>The window is up: an update waiting to hear that can finish.</summary>
    void Started();
}

/// <param name="App">The running bundle.</param>
/// <param name="Home">This app's home, which holds the update's folder.</param>
/// <param name="Database">This app's database.</param>
/// <param name="Version">The running version.</param>
internal sealed record AppLocation(string App, string Home, string Database, string Version)
{
    internal string Folder => Path.Combine(Home, "app-update");
    internal string Failed => Path.Combine(Folder, "failed.json");
}

/// <summary>
/// Everything before the swap. The stage sits beside the bundle so the swap stays on one volume. It's made and checked with
/// nothing stopped, and waits there: only Restart to Update goes on, to the wait for sessions and the quit. The helper does
/// the rest once this app has gone, starting with the copy of the database, so no write comes after it.
/// </summary>
internal sealed partial class AppUpdate(AppLocation location, RemoteReleaseFeed feed, Func<CancellationToken, Task> idle, Func<bool> busy,
    Func<string, string, CancellationToken, Task<Action>> startHelper, Func<int> windowPid, Action quit) : IAppInstall
{
    private const string Bundle = "Assembly.app", StagePrefix = ".assembly-update-";
    internal const string WaitingWords = "Waiting for running sessions to finish…";
    private sealed record Staged(string Version, string Stage, string Fresh, string Program);
    private volatile Staged? _staged;

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
        _staged = null;
        try
        {
            // What an earlier run got ready and no one restarted into: this run fetches it again.
            foreach (var left in Directory.EnumerateDirectories(parent, StagePrefix + "*")) Directory.Delete(left, recursive: true);
            progress($"Downloading Assembly {version}…");
            var release = await feed.AppReleaseAsync(version, ct);
            var unpacked = Path.Combine(stage, "new");
            Directory.CreateDirectory(unpacked);
            var archive = Path.Combine(stage, "app.tar.gz");
            await feed.DownloadAppAsync(release, archive, ct);
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
            var copy = Path.Combine(location.Folder, $"{AppSwap.BackupPrefix}{staged.Version}.db");
            var planFile = Path.Combine(staged.Stage, "plan.json");
            var helper = Path.Combine(staged.Fresh, "Contents/Resources/backend", Path.GetFileName(Environment.ProcessPath ?? "assembly"));
            while (true)
            {
                progress(WaitingWords);
                await idle(ct);
                var plan = AppSwapPlan.For(location, staged.Fresh, staged.Program, staged.Stage, staged.Version, copy, windowPid());
                await File.WriteAllBytesAsync(planFile, JsonSerializer.SerializeToUtf8Bytes(plan, AppSwapJson.Default.AppSwapPlan), ct);
                var callOff = await startHelper(helper, planFile, ct);
                // Starting the helper can take seconds, and a turn can start in them. Quitting now would kill it.
                if (!busy())
                {
                    progress($"Restarting into Assembly {staged.Version}…");
                    quit();
                    return;
                }
                callOff();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CallFailed($"Assembly can't replace itself in {Path.GetDirectoryName(location.App)}, so nothing was changed: {error.Message}", "app_update_failed");
        }
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
        var copy = Path.Combine(Path.GetDirectoryName(plan)!, "finish-update");
        File.Copy(helper, copy, overwrite: true);
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
        if (said == AppSwap.Ready) return CallOff;
        CallOff();
        throw new CallFailed("The update helper didn't start.", "app_update_failed");
    }

    [GeneratedRegex(@"<key>CFBundleExecutable</key>\s*<string>([^<]*)</string>")]
    private static partial Regex Executable();
    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]*)</string>")]
    private static partial Regex ShortVersion();
}
