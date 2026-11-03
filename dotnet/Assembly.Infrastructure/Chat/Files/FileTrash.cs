using System.ComponentModel;
using System.Diagnostics;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Host Trash only. Unavailable desktop integration is a refusal, never permanent deletion.</summary>
public sealed class FileTrash
{
    private readonly Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<bool>> run;
    public FileTrash() : this(RunAsync) { }
    internal FileTrash(Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<bool>> run) => this.run = run;

    public async Task<ActionResult> TrashAsync(string fullPath, CancellationToken ct)
    {
        const string script = "on run argv\nset targetPath to item 1 of argv\ntell application \"Finder\" to delete POSIX file targetPath\nend run\n";
        var applied = OperatingSystem.IsMacOS()
            ? await run("/usr/bin/osascript", ["-", fullPath], script, ct)
            : OperatingSystem.IsLinux() && await run("gio", ["trash", "--", fullPath], null, ct);
        return applied ? new ActionResult("applied", null, null) : new ActionResult("failed", null,
            new FileFault("trash_unavailable", "The host could not move this item to Trash. The file was not permanently deleted."));
    }

    private static async Task<bool> RunAsync(string executable, IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception) { return false; }
        if (process is null) return false;
        using (process)
        using (ct.Register(() => Stop(process)))
        {
            // Drains belong to this process and must finish after the exact stop/join, even on cancellation.
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            try
            {
                if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), ct);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
                await Task.WhenAll(output, error);
                return process.ExitCode == 0;
            }
            finally
            {
                Stop(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
            }
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
