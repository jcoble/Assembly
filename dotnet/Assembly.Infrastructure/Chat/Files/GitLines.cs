using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// Runs git in a folder and reads its NUL-separated output as it comes. The process is killed when the token fires or
/// the reader stops early, so a search nobody wants doesn't keep walking.
/// </summary>
internal static class GitLines
{
    public static async IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // English messages, so a caller can tell one failure from another.
        start.Environment["LC_ALL"] = "C";
        using var git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        // Killing it ends its output, so a read waiting on it returns and the token's throw below runs.
        using var stop = cancellationToken.Register(() => Kill(git));
        try
        {
            var stderr = git.StandardError.ReadToEndAsync(CancellationToken.None);
            // Written beside the read, so neither pipe fills while the other waits.
            var written = WriteAsync(git.StandardInput, input);
            var record = new StringBuilder();
            var buffer = new char[16 * 1024];
            int read;
            while ((read = await git.StandardOutput.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != '\0')
                    {
                        record.Append(buffer[i]);
                        continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return record.ToString();
                    record.Clear();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await written;
            await git.WaitForExitAsync(cancellationToken);
            // 1 is how git says nothing matched (check-ignore with nothing ignored); above that it failed.
            if (git.ExitCode > 1)
                throw new CallFailed($"git {arguments[0]} failed: {(await stderr).Trim()}");
        }
        finally
        {
            Kill(git);
            await git.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static async Task WriteAsync(StreamWriter stdin, IReadOnlyList<string>? input)
    {
        try
        {
            foreach (var line in input ?? [])
            {
                await stdin.WriteAsync(line);
                await stdin.WriteAsync('\0');
            }
            // Closing flushes what's buffered, so it too meets a git that has stopped.
            stdin.Close();
        }
        catch (IOException)
        {
            // git stopped reading: it failed or was killed, and the read says why.
        }
    }

    private static void Kill(Process git)
    {
        try
        {
            if (!git.HasExited)
                git.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It had already exited.
        }
    }
}
