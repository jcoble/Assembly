using System.Diagnostics;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Features.Preferences;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Agents;

/// <summary>The utility model run through a signed-in CLI, here fake <c>claude</c> and <c>codex</c> scripts.</summary>
public sealed class CliUtilityModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _dir = Directory.CreateTempSubdirectory("assembly-utility-").FullName;
    private readonly TempDatabase _db = new();
    private readonly AppDatabase _database;
    private readonly PreferenceStore _preferences;
    private readonly SharedLog _log = new();

    public CliUtilityModelTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _preferences = new PreferenceStore(_database);
    }

    private string Claude => Path.Combine(_dir, "claude");

    private string Codex => Path.Combine(_dir, "codex");

    private string Missing => Path.Combine(_dir, "missing");

    [Fact]
    public async Task Claude_s_json_result_is_the_answer_and_its_api_key_is_not_passed_on()
    {
        Script(Claude, """printf '{"type":"result","result":"Fix rail titles"}'""");
        var model = new CliUtilityModel(Claude, Missing, _preferences, TimeProvider.System, _log);

        string? answer;
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "a-key");
        try
        {
            answer = await model.CompleteAsync("Name it", CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }

        Assert.Equal("Fix rail titles", answer);
        Assert.Equal("-p --safe-mode --no-session-persistence --tools  --model claude-haiku-4-5 --output-format json", Recorded("args"));
        Assert.Equal("Name it", Recorded("stdin"));
        Assert.Equal("unset unset unset", Recorded("env"));
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd('/'), Recorded("cwd"));
    }

    [Fact]
    public async Task Codex_s_trimmed_output_is_the_answer_and_its_api_keys_are_not_passed_on()
    {
        Script(Codex, """printf '  Fix rail titles  \n'""");
        _preferences.Save(JsonDocument.Parse("""{"utilityModel":"codex"}""").RootElement);
        var model = new CliUtilityModel(Claude, Codex, _preferences, TimeProvider.System, _log);

        string? answer;
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "a-key");
        Environment.SetEnvironmentVariable("CODEX_API_KEY", "a-key");
        try
        {
            answer = await model.CompleteAsync("Name it", CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("CODEX_API_KEY", null);
        }

        Assert.Equal("Fix rail titles", answer);
        Assert.Equal("exec --sandbox read-only --ephemeral --ignore-user-config --ignore-rules --skip-git-repo-check --model gpt-5.6-luna -", Recorded("args"));
        Assert.Equal("Name it", Recorded("stdin"));
        Assert.Equal("unset unset unset", Recorded("env"));
    }

    [Fact]
    public async Task With_no_setting_the_first_cli_that_runs_is_used()
    {
        Script(Codex, "printf 'From codex'");
        var model = new CliUtilityModel(Missing, Codex, _preferences, TimeProvider.System, _log);

        Assert.Equal("From codex", await model.CompleteAsync("Name it", CancellationToken.None));
    }

    [Fact]
    public async Task Turned_off_it_runs_nothing_and_says_so()
    {
        Script(Claude, "printf 'From claude'");
        _preferences.Save(JsonDocument.Parse("""{"utilityModel":"off"}""").RootElement);
        var model = new CliUtilityModel(Claude, Missing, _preferences, TimeProvider.System, _log);

        Assert.Null(await model.CompleteAsync("Name it", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "args")));
        Assert.Contains("utility model: none set up", _log.ToString());
    }

    [Fact]
    public async Task A_failed_run_answers_nothing_and_logs_its_error()
    {
        Script(Claude, "echo 'not signed in' >&2; exit 1");
        var model = new CliUtilityModel(Claude, Missing, _preferences, TimeProvider.System, _log);

        Assert.Null(await model.CompleteAsync("Name it", CancellationToken.None));
        Assert.Contains("not signed in", _log.ToString());
    }

    [Fact]
    public async Task A_backend_stop_while_it_runs_kills_its_process_group()
    {
        var pidFifo = await FifoAsync("pid");
        var blockFifo = await FifoAsync("block");
        Script(Claude, $"echo $$ > '{pidFifo}'; read line < '{blockFifo}'");
        var model = new CliUtilityModel(Claude, Missing, _preferences, TimeProvider.System, _log);
        var work = new BackgroundWork(TimeProvider.System, _log);

        work.Start("title", async stop => await model.CompleteAsync("Name it", stop));
        var pid = int.Parse((await Task.Run(() => File.ReadAllTextAsync(pidFifo)).WaitAsync(Wait)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, await work.StopAsync(Wait));

        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task A_backend_stop_while_it_checks_which_cli_runs_kills_the_check()
    {
        var pidFifo = await FifoAsync("pid");
        var blockFifo = await FifoAsync("block");
        File.WriteAllText(Claude, $"#!/bin/sh\necho $$ > '{pidFifo}'; read line < '{blockFifo}'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var model = new CliUtilityModel(Claude, Missing, _preferences, TimeProvider.System, _log);
        var work = new BackgroundWork(TimeProvider.System, _log);

        work.Start("title", async stop =>
        {
            await Task.Yield();
            await model.CompleteAsync("Name it", stop);
        });
        var pid = int.Parse((await Task.Run(() => File.ReadAllTextAsync(pidFifo)).WaitAsync(Wait)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        // Well inside the check's own ten seconds.
        Assert.Equal(0, await work.StopAsync(TimeSpan.FromSeconds(3)));

        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    // A fake CLI: it answers --version, and otherwise records its arguments, input, folder and API keys, then runs body.
    private void Script(string path, string body)
    {
        File.WriteAllText(path, $$"""
            #!/bin/sh
            [ "$1" = --version ] && exit 0
            echo "$*" > '{{_dir}}/args'
            cat > '{{_dir}}/stdin'
            pwd > '{{_dir}}/cwd'
            echo "${ANTHROPIC_API_KEY-unset} ${OPENAI_API_KEY-unset} ${CODEX_API_KEY-unset}" > '{{_dir}}/env'
            {{body}}

            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string Recorded(string name) => File.ReadAllText(Path.Combine(_dir, name)).TrimEnd('\n');

    private async Task<string> FifoAsync(string name)
    {
        var path = Path.Combine(_dir, name);
        using var mkfifo = Process.Start("/usr/bin/mkfifo", [path]);
        await mkfifo.WaitForExitAsync();
        return path;
    }

    public void Dispose()
    {
        _database.Dispose();
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}
