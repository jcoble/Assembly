using Assembly.Infrastructure.Chat;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Terminal;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Draft.Terminal;

public sealed class TerminalHostTests : IAsyncDisposable
{
    private readonly FakePtyStarter _pty = new();
    private readonly RecordingTopics _topics = new();
    private readonly TerminalHost _host;
    private readonly string _folder = Directory.CreateTempSubdirectory("assembly-terminal-").FullName;

    public TerminalHostTests() => _host = new TerminalHost(_pty, _topics, new FakeTimeProvider(), ("/bin/sh", null), _ => _folder);

    [Fact]
    public async Task The_last_output_goes_out_before_the_exit_and_stays_readable_until_close()
    {
        // Pins: terminal#7
        var terminal = await _host.StartAsync(new StartArgs(_folder));
        _pty.Started[0].Emit("bye"u8.ToArray());
        _pty.Started[0].End(3);

        await _topics.WaitFor(p => p.Topic == TerminalFeature.OutputTopic && p.Json.Contains("\"exit\":"));

        var published = _topics.Snapshot();
        var lastRow = published.FindLastIndex(p => p.Topic == TerminalFeature.OutputTopic && p.Json.Contains("\"data\":"));
        var exit = published.FindIndex(p => p.Topic == TerminalFeature.OutputTopic && p.Json.Contains("\"exit\":"));
        Assert.True(lastRow >= 0 && lastRow < exit, "the exit went out before the last output");
        Assert.Equal(3, Assert.Single(_host.List()).ExitCode);
        var exitRow = published[exit].Json;
        Assert.Equal((4L, 3L), (Seq(exitRow), PrevSeq(exitRow)));
        Assert.Equal("bye"u8.ToArray(), _host.Read(terminal.Id, 0).Data);
    }

    [Fact]
    public async Task Closing_ends_the_program_lets_go_of_its_output_and_is_ok_twice()
    {
        // Pins: terminal#8
        var terminal = await _host.StartAsync(new StartArgs(_folder));

        await _host.CloseAsync(terminal.Id);
        await _host.CloseAsync(terminal.Id);

        Assert.True(_pty.Started[0].Disposed);
        Assert.Empty(_host.List());
        Assert.Throws<CallFailed>(() => _host.Read(terminal.Id, 0));
    }

    [Fact]
    public async Task Stopping_the_host_closes_every_terminal()
    {
        // Pins: terminal#8
        await _host.StartAsync(new StartArgs(_folder));
        await _host.StartAsync(new StartArgs(_folder));

        await _host.DisposeAsync();

        Assert.All(_pty.Started, pty => Assert.True(pty.Disposed));
    }

    [Fact]
    public async Task A_start_that_arrives_after_the_host_stopped_fails_and_leaves_nothing_running()
    {
        // Pins: terminal#8
        await _host.DisposeAsync();

        await Assert.ThrowsAsync<CallFailed>(() => _host.StartAsync(new StartArgs(_folder)));
        Assert.All(_pty.Started, pty => Assert.True(pty.Disposed));
    }

    [Fact]
    public async Task A_session_this_machine_doesn_t_run_gets_no_terminal_even_with_a_folder()
    {
        // Pins: terminal#12
        await using var host = new TerminalHost(_pty, _topics, new FakeTimeProvider(), ("/bin/sh", null), _ => throw new CallFailed("on another machine"));

        await Assert.ThrowsAsync<CallFailed>(() => host.StartAsync(new StartArgs(_folder, Owner: "remote-1")));
        Assert.Empty(_pty.Started);
    }

    [Fact]
    public async Task What_you_type_reaches_the_program_until_it_ends()
    {
        // Pins: terminal#2
        var terminal = await _host.StartAsync(new StartArgs(_folder));

        await _host.WriteAsync(terminal.Id, [0x03], CancellationToken.None);
        _pty.Started[0].End(130);
        await _topics.WaitFor(p => p.Topic == TerminalFeature.OutputTopic && p.Json.Contains("\"exit\":"));

        Assert.Equal(new byte[] { 0x03 }, Assert.Single(_pty.Started[0].Written));
        await Assert.ThrowsAsync<CallFailed>(async () => await _host.WriteAsync(terminal.Id, [0x03], CancellationToken.None));
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("remote", false)]
    [InlineData("remote", true)]
    [InlineData("helper", false)]
    [InlineData("helper", true)]
    public async Task Production_ownership_rejects_nonlocal_or_unknown_owners_before_start(string kind, bool explicitFolder)
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        var sessions = Assembly.Application.Features.Chat.Sessions.SessionStore.Open(db, TimeProvider.System, _folder);
        var remote = sessions.Add("remote", "agent", "https://example.invalid/repo", "workbox-test");
        var helper = sessions.Helper(remote, "agent", "Remote helper");
        var owner = kind == "helper" ? helper.SessionId : kind;
        await using var host = new TerminalHost(_pty, _topics, new FakeTimeProvider(), ("/bin/sh", null),
            Assembly.Infrastructure.Chat.SessionTerminalFolders.ExistingFolder(_folder, sessions));
        await Assert.ThrowsAsync<CallFailed>(() => host.StartAsync(new StartArgs(explicitFolder ? _folder : null, Owner: owner)));
        Assert.Empty(_pty.Started);
    }

    [Fact]
    public async Task A_nested_helpers_shell_starts_in_its_top_sessions_folder()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        var sessions = Assembly.Application.Features.Chat.Sessions.SessionStore.Open(db, TimeProvider.System, _folder);
        var top = sessions.Create("agent", "https://example.invalid/repo");
        var nested = sessions.Helper(sessions.Helper(top, "agent", "Helper"), "agent", "Nested helper");
        var topFolder = Directory.CreateDirectory(Path.Combine(_folder, top.SessionId)).FullName;
        await using var host = new TerminalHost(_pty, _topics, new FakeTimeProvider(), ("/bin/sh", null),
            Assembly.Infrastructure.Chat.SessionTerminalFolders.ExistingFolder(_folder, sessions));

        await host.StartAsync(new StartArgs(null, Owner: nested.SessionId));

        Assert.Equal(topFolder, Assert.Single(_pty.Started).Request.Cwd);
    }

    [Fact]
    public async Task Closing_the_host_during_start_disposes_the_late_pty()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        _pty.BeforeStart = () => { entered.SetResult(); release.Wait(); };
        var starting = Task.Run(() => _host.StartAsync(new StartArgs(_folder)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await _host.DisposeAsync(); }
        finally { release.Set(); }
        await Assert.ThrowsAsync<CallFailed>(() => starting);
        Assert.True(Assert.Single(_pty.Started).Disposed);
    }

    [Fact]
    public async Task Closing_with_a_read_and_write_pending_ends_both()
    {
        var terminal = await _host.StartAsync(new StartArgs(_folder));
        var pty = _pty.Started[0];
        pty.HoldWrites = true;
        var writing = _host.WriteAsync(terminal.Id, [1], CancellationToken.None).AsTask();
        await pty.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _host.CloseAsync(terminal.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(pty.Disposed);
        Assert.Empty(_host.List());
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private static long Seq(string row) => JsonDocument.Parse(row).RootElement.GetProperty("seq").GetInt64();

    private static long PrevSeq(string row) => JsonDocument.Parse(row).RootElement.GetProperty("prevSeq").GetInt64();

    private static byte[] Data(string row) => JsonDocument.Parse(row).RootElement.GetProperty("data").GetBytesFromBase64();
}
