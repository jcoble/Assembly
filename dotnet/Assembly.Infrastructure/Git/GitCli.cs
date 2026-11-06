using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Infrastructure.Git;

/// <summary>
/// Runs git in a folder and reads its NUL-separated output as it comes. The process is killed when the token fires or
/// the reader stops early, so a search nobody wants doesn't keep walking.
/// </summary>
public sealed class GitCli(TimeProvider? time = null) : IGit
{
    /// <summary>Size and write time: a stat, not a read, so a status read stays cheap however big the files are.</summary>
    public string Revision(string folder, string path)
    {
        var file = new FileInfo(Path.Combine(folder, path));
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "none";
    }
    public async IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Start(folder, arguments);
        using var git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var tree = new ProcessTree(git);
        // Killing it ends its output, so a read waiting on it returns and the token's throw below runs.
        using var stop = cancellationToken.Register(tree.Kill);
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
            tree.Kill();
            await git.WaitForExitAsync(CancellationToken.None);
            await tree.GoneAsync();
        }
    }

    public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? stdin,
        TimeSpan? limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Count == 0) throw new ArgumentException("A git action is required.", nameof(arguments));
        var budget = limit ?? TimeSpan.FromSeconds(60);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
        using var deadline = new CancellationTokenSource(budget, time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var start = Start(folder, arguments);
        start.Environment["GIT_EDITOR"] = "true";
        Process git;
        try { git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2)
        { throw new CallFailed("Git isn't installed on this machine."); }
        var tree = new ProcessTree(git);
        using (git)
        using (linked.Token.Register(tree.Kill))
        {
            var output = ReadBoundedAsync(git.StandardOutput.BaseStream);
            var error = ReadBoundedAsync(git.StandardError.BaseStream);
            var written = WriteExactAsync(git.StandardInput, stdin, linked.Token);
            try
            {
                await git.WaitForExitAsync(CancellationToken.None);
                await written;
                var stdout = await output;
                var stderr = await error;
                linked.Token.ThrowIfCancellationRequested();
                return new(git.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new CallFailed($"git {arguments[0]} took longer than {budget.TotalSeconds:0.#} seconds"); }
            finally
            {
                tree.Kill();
                await git.WaitForExitAsync(CancellationToken.None);
                try { await written; }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                await Task.WhenAll(output, error);
                await tree.GoneAsync();
            }
        }
    }

    private static ProcessStartInfo Start(string folder, IReadOnlyList<string> arguments)
    {
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
        // English messages, so a caller can tell one failure from another.
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<string> ReadBoundedAsync(Stream stream)
    {
        const int cap = 64 * 1024;
        var kept = new byte[cap];
        var buffer = new byte[16 * 1024];
        var count = 0;
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var take = Math.Min(read, cap - count);
            buffer.AsSpan(0, take).CopyTo(kept.AsSpan(count)); count += take;
            truncated |= read > take;
        }
        // flush:false preserves a complete UTF8 prefix when the cap cuts a multibyte character.
        var chars = new char[count];
        Encoding.UTF8.GetDecoder().Convert(kept.AsSpan(0, count), chars, !truncated, out _, out var used, out _);
        return new string(chars, 0, used) + (truncated ? "…" : "");
    }
    private static async Task WriteExactAsync(StreamWriter writer, string? text, CancellationToken ct)
    {
        try
        {
            if (text is not null) await writer.WriteAsync(text.AsMemory(), ct);
            writer.Close();
        }
        catch (IOException) { } // An exited/killed git owns the actual exit/result.
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
}
