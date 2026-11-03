using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.GitHub;

namespace Assembly.Infrastructure.Git;

/// <summary>One owned, noninteractive GitHub CLI invocation with a hard response budget.</summary>
public sealed class GhCli(string executable = "gh", TimeProvider? time = null) : IGh
{
    public async Task<GhRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? stdinJson, TimeSpan limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
        using var deadline = new CancellationTokenSource(limit, time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process gh;
        try { gh = Process.Start(start) ?? throw new InvalidOperationException("Could not start GitHub CLI."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2)
        { throw new CallFailed("GitHub CLI (gh) isn't installed on this machine. Install it and run gh auth login."); }
        using (gh)
        using (linked.Token.Register(() => Kill(gh)))
        {
            var totalBytes = 0;
            var output = Read(gh.StandardOutput.BaseStream);
            var stderr = Read(gh.StandardError.BaseStream);
            var written = Write();
            try
            {
                await Task.WhenAll(output, stderr, written);
                await gh.WaitForExitAsync(CancellationToken.None);
                linked.Token.ThrowIfCancellationRequested();
                var bytes = await output;
                return new(gh.ExitCode, Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(await stderr)) { OutputBytes = bytes };
            }
            catch (OperationCanceledException error) when (linked.IsCancellationRequested)
            { throw new GhInterrupted("GitHub's completion was interrupted.", error, linked.Token); }
            catch (IOException error)
            { throw new GhInterrupted("GitHub's completion was lost.", error, linked.Token); }
            finally
            {
                Kill(gh);
                await gh.WaitForExitAsync(CancellationToken.None);
            }
            async Task<byte[]> Read(Stream stream)
            {
                using var kept = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int count;
                while ((count = await stream.ReadAsync(buffer, CancellationToken.None)) > 0)
                {
                    if (Interlocked.Add(ref totalBytes, count) > 2_000_000)
                    { Kill(gh); throw new CallFailed("GitHub's answer was too large"); }
                    kept.Write(buffer, 0, count);
                }
                return kept.ToArray();
            }
            async Task Write()
            {
                try
                {
                    if (stdinJson is not null) await gh.StandardInput.WriteAsync(stdinJson.AsMemory(), linked.Token);
                    gh.StandardInput.Close();
                }
                catch (IOException) { } // Its exit code and stderr own the refusal.
            }
        }
    }
    private static void Kill(Process gh)
    {
        try { if (!gh.HasExited) gh.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
