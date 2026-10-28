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
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(120);

    private readonly Cli[] _clis;
    private readonly Lazy<Cli?> _found;
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
        _found = new(() => _clis.FirstOrDefault(cli => Runs(cli.File)));
    }

    // One signed-in CLI: how to run it, and the API keys it must not see.
    private sealed record Cli(string Name, string File, string[] Args, string[] ApiKeys, bool Json);

    public async Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var setting = Setting();
        var cli = setting == "off" ? null : _clis.FirstOrDefault(known => known.Name == setting) ?? _found.Value;
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
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
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
            : process.ExitCode != 0 ? $"exit {process.ExitCode}: {string.Concat(errors.Trim().Take(300))}"
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

    private static bool Runs(string cli)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(cli, ["--version"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            if (process.WaitForExit(TimeSpan.FromSeconds(10)))
                return process.ExitCode == 0;
            process.Kill(entireProcessTree: true);
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
