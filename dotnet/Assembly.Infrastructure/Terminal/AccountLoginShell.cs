using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Assembly.Infrastructure.Terminal;

/// <summary>Reads the effective account's configured shell once, with an owned, bounded lookup child.</summary>
internal static class AccountLoginShell
{
    internal const string Unavailable = "the configured account login shell is unavailable";
    internal const string Lookup = "my @p=getpwuid($<); print $p[8] // q{};";
    private const int OutputLimit = 4096;

    internal static Task<(string? Path, string? Error)> ReadAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/perl"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(Lookup);
        return ReadAsync(start, ct);
    }

    internal static async Task<(string? Path, string? Error)> ReadAsync(ProcessStartInfo start, CancellationToken ct, Action<int>? started = null)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        Process? child = null;
        try
        {
            start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = false;
            child = Process.Start(start) ?? throw new IOException("Account shell lookup did not start.");
            started?.Invoke(child.Id);
            var text = new StringBuilder(); var buffer = new char[256]; int read;
            while ((read = await child.StandardOutput.ReadAsync(buffer, stop.Token)) != 0)
            {
                if (text.Length + read > OutputLimit) throw new IOException("Account shell lookup output exceeded its bound.");
                text.Append(buffer, 0, read);
            }
            await child.WaitForExitAsync(stop.Token);
            ct.ThrowIfCancellationRequested();
            var path = text.ToString().Trim();
            return child.ExitCode == 0 && IsUsable(path) ? (path, null) : (null, Unavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, Unavailable); }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException) { return (null, Unavailable); }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        try { child.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (child.HasExited) { }
                    }
                    await child.WaitForExitAsync(CancellationToken.None);
                }
                finally { child.Dispose(); }
            }
        }
    }

    internal static bool IsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) || OperatingSystem.IsWindows()) return false;
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
