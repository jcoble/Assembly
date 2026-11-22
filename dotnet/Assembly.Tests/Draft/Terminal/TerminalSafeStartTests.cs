using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Terminal;

namespace Assembly.Tests.Draft.Terminal;

public sealed class TerminalSafeStartTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("assembly-terminal-safe-").FullName;

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

    public void Dispose() => Directory.Delete(_folder, true);
}
