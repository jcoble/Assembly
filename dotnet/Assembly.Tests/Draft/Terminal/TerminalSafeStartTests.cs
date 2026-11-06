using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Terminal;

namespace Assembly.Tests.Draft.Terminal;

public sealed class TerminalSafeStartTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("assembly-terminal-safe-").FullName;

    [Fact]
    public async Task Owned_shell_uses_its_actual_folder_and_account_shell_and_refuses_a_conflicting_cwd()
    {
        var starter = new FakePtyStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System, ("/bin/sh", null), _ => _folder);
        await Assert.ThrowsAsync<CallFailed>(() => host.StartAsync(new StartArgs(Path.GetTempPath(), Owner: "session")));
        Assert.Empty(starter.Started);
        var made = await host.StartAsync(new StartArgs(Owner: "session"));
        Assert.Equal(_folder, made.Cwd);
        Assert.Equal("/bin/sh", Assert.Single(starter.Started).Request.FileName);
        Assert.Equal(new[] { "-l" }, starter.Started[0].Request.Arguments);
    }

    [Fact]
    public async Task Missing_or_unmaterialized_owned_folder_never_starts_a_pty_or_falls_back_home()
    {
        var starter = new FakePtyStarter();
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var sessions = Assembly.Application.Features.Chat.Sessions.SessionStore.Open(db, TimeProvider.System, _folder);
        var missing = sessions.Create("fixture", "", workingFolder: _folder + "/absent");
        var unmaterialized = sessions.Create("fixture", "");
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System, ("/bin/sh", null),
            Assembly.Infrastructure.Chat.SessionTerminalFolders.ExistingFolder(_folder, sessions));
        Assert.Contains(_folder + "/absent", (await Assert.ThrowsAsync<CallFailed>(() => host.StartAsync(new StartArgs(Owner: missing.SessionId)))).Message);
        await Assert.ThrowsAsync<CallFailed>(() => host.StartAsync(new StartArgs(Owner: unmaterialized.SessionId)));
        Assert.Empty(starter.Started);
        Assert.False(Directory.Exists(_folder + "/absent"));
    }

    [Fact]
    public async Task Retained_default_shell_error_does_not_block_explicit_shell_program_or_existing_utility_reuse()
    {
        var starter = new FakePtyStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System,
            (null, "the configured account login shell is unavailable"), _ => _folder);
        var error = await Assert.ThrowsAsync<CallFailed>(() => host.StartAsync(new StartArgs(Owner: "session")));
        Assert.Equal("the configured account login shell is unavailable", error.Message);
        Assert.Empty(starter.Started);
        var made = await host.StartAsync(new StartArgs(Shell: "/bin/sh", Owner: "session"));
        Assert.Equal(made.Id, (await host.StartAsync(new StartArgs(Owner: "session"))).Id);
        var program = await host.StartProgramAsync(new ProgramStart("/bin/sh", ["-c", "printf program-ok"], new Dictionary<string, string?>(), _folder, "signin:operation"), default);
        Assert.Equal(2, starter.Started.Count);
        Assert.NotEqual(made.Id, program.Id);
        Assert.Equal(new[] { "-c", "printf program-ok" }, starter.Started[1].Request.Arguments);
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
