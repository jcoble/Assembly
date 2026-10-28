using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Preferences;

namespace Assembly.Infrastructure.Agents;

/// <summary>
/// The utility model, run through the signed-in Claude Code or Codex CLI (the files given), with no API
/// key: the <c>utilityModel</c> preference picks <c>claude</c>, <c>codex</c> or <c>off</c>. With none saved, it's the
/// first of the two whose <c>--version</c> runs, checked once. Each run leads its own process group, in the temp folder,
/// with the prompt on its stdin, and the whole group is killed when the token is cancelled or after two minutes.
/// </summary>
internal sealed class CliUtilityModel : IUtilityModel
{
    private const int OutputLimit = 64 * 1024;
    private const int ErrorsKept = 300;
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(120);

    private readonly Cli[] _clis;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private bool _checked;
    private Cli? _found;
    private readonly PreferenceStore _preferences;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;

    public CliUtilityModel(string claude, string codex, PreferenceStore preferences, TimeProvider time, TextWriter log)
    {
        _preferences = preferences;
        _time = time;
        _log = log;
        // Claude Code answers in JSON, its result the answer; Codex prints the answer alone.
        _clis =
        [
            new("claude", claude, ["-p", "--safe-mode", "--no-session-persistence", "--tools", "", "--model", "claude-haiku-4-5", "--output-format", "json"],
                ["ANTHROPIC_API_KEY"], Json: true),
            new("codex", codex,
                ["exec", "--sandbox", "read-only", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check", "--model", "gpt-5.6-luna", "-"],
                ["OPENAI_API_KEY", "CODEX_API_KEY"], Json: false),
        ];
    }

    // One signed-in CLI: how to run it, and the API keys it must not see.
    private sealed record Cli(string Name, string File, string[] Args, string[] ApiKeys, bool Json);

    public async Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var setting = Setting();
        var cli = setting == "off" ? null : _clis.FirstOrDefault(known => known.Name == setting) ?? await FoundAsync(cancellationToken);
        if (cli is null)
        {
            _log.WriteLine("utility model: none set up, so sessions keep the title their first message gave");
            return null;
        }

        var command = ProcessGroup.Leading(new AgentCommand(cli.File, cli.Args, Path.GetTempPath()));
        var start = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);
        // The signed-in subscription answers, never a key that bills the API.
        foreach (var key in cli.ApiKeys)
            start.Environment.Remove(key);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command.FileName}.");
        using var limit = new CancellationTokenSource(RunLimit, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        var output = new byte[OutputLimit + 1];
        int read;
        string errors;
        using (either.Token.Register(() => ProcessGroup.Kill(process.Id)))
        {
            var stderr = KeepStartAsync(process.StandardError);
            try
            {
                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It ended without reading it; its exit says why.
            }

            read = await process.StandardOutput.BaseStream.ReadAtLeastAsync(output, output.Length, throwOnEndOfStream: false, CancellationToken.None);
            if (read > OutputLimit)
                ProcessGroup.Kill(process.Id);
            errors = await stderr;
            await process.WaitForExitAsync(CancellationToken.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var failure = limit.IsCancellationRequested ? $"no answer in {RunLimit.TotalSeconds:0} s"
            : read > OutputLimit ? "an answer over 64 KiB"
            : process.ExitCode != 0 ? $"exit {process.ExitCode}: {errors.Trim()}"
            : null;
        var raw = Encoding.UTF8.GetString(output, 0, Math.Min(read, OutputLimit));
        var answer = failure is null ? (cli.Json ? Result(raw) : raw.Trim()) : null;
        if (string.IsNullOrEmpty(answer))
        {
            _log.WriteLine($"utility model: {cli.Name} gave {failure ?? "no answer"}");
            return null;
        }

        return answer;
    }

    private string? Setting()
    {
        if (_preferences.Read("utilityModel") is not { } saved)
            return null;
        using var json = JsonDocument.Parse(saved);
        return json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() : null;
    }

    // The JSON output's result.
    private static string? Result(string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("result", out var result)
                   && result.ValueKind == JsonValueKind.String
                ? result.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The first CLI whose --version runs, checked once; a check a stop cut short is made again next time.
    private async Task<Cli?> FoundAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken);
        try
        {
            if (!_checked)
            {
                foreach (var cli in _clis)
                    if (await RunsAsync(cli.File, cancellationToken))
                    {
                        _found = cli;
                        break;
                    }
                _checked = true;
            }
            return _found;
        }
        finally
        {
            _checking.Release();
        }
    }

    private async Task<bool> RunsAsync(string cli, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(cli, ["--version"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
        }
        catch (Win32Exception)
        {
            return false;
        }

        using (process)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10), _time);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
            try
            {
                await process.WaitForExitAsync(either.Token);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }
    }

    // The first few hundred characters of a stream, read to its end, so a noisy CLI can't fill memory.
    private static async Task<string> KeepStartAsync(StreamReader reader)
    {
        var kept = new char[ErrorsKept];
        var length = await reader.ReadBlockAsync(kept, CancellationToken.None);
        await reader.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        return new string(kept, 0, length);
    }
}
