using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Infrastructure.Git;

/// <summary>
/// Runs git in a folder and reads its NUL-separated output as it comes. The process is killed when the token fires or
/// the reader stops early, so a search nobody wants doesn't keep walking.
/// </summary>
public sealed class GitCli : IGit
{
    public async IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(folder);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
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
                        if (record.Length == 1024 * 1024)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return record.ToString();
                            record.Clear();
                        }
                        continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return record.ToString();
                    record.Clear();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Length > 0)
                yield return record.ToString();
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
        }
        catch (IOException)
        {
            // git stopped reading: it was killed, and the read says why.
        }
        finally
        {
            stdin.Close();
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
