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

    public TerminalHostTests() => _host = new TerminalHost(_pty, _topics, new FakeTimeProvider());

    [Fact]
    public async Task Each_read_goes_out_as_one_row_with_its_byte_offsets_and_its_bytes_unchanged()
    {
        // Pins: terminal#3
        var terminal = await _host.StartAsync(new StartArgs(_folder));
        _pty.Started[0].Emit(0xe4, 0xb8);
        _pty.Started[0].Emit(0x96);

        await _topics.WaitFor(p => p.Topic == TerminalFeature.OutputTopic && Seq(p.Json) == 3);

        var rows = Rows(terminal.Id);
        Assert.Equal([(2L, 0L), (3L, 2L)], rows.Select(row => (Seq(row), PrevSeq(row))));
        Assert.Equal(new byte[] { 0xe4, 0xb8 }, Data(rows[0]));
        Assert.Equal(new byte[] { 0x96 }, Data(rows[1]));
    }

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
    public async Task A_session_asking_again_gets_its_running_terminal_and_a_new_one_once_that_has_ended()
    {
        // Pins: terminal#9
        var first = await _host.StartAsync(new StartArgs(_folder, Owner: "session-1"));
        var again = await _host.StartAsync(new StartArgs(_folder, Owner: "session-1"));
        Assert.Equal(first.Id, again.Id);
        Assert.Single(_pty.Started);

        _pty.Started[0].End(0);
        await _topics.WaitFor(p => p.Topic == TerminalFeature.OutputTopic && p.Json.Contains("\"exit\":"));

        var next = await _host.StartAsync(new StartArgs(_folder, Owner: "session-1"));
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(2, _pty.Started.Count);
    }

    [Fact]
    public async Task A_command_run_for_a_session_is_never_handed_back_as_its_shell()
    {
        // Pins: terminal#9
        var run = await _host.StartAsync(new StartArgs(_folder, Command: "make test", Owner: "session-1"));
        var shell = await _host.StartAsync(new StartArgs(_folder, Owner: "session-1"));
        var again = await _host.StartAsync(new StartArgs(_folder, Command: "make test", Owner: "session-1"));

        Assert.Equal(3, new[] { run.Id, shell.Id, again.Id }.Distinct().Count());
    }

    [Fact]
    public async Task A_size_is_kept_between_20_and_300_columns_and_4_and_100_rows()
    {
        // Pins: terminal#4
        var plain = await _host.StartAsync(new StartArgs(_folder));
        var odd = await _host.StartAsync(new StartArgs(_folder, Cols: 5, Rows: 500));
        _host.Resize(odd.Id, 1000, 1);

        Assert.Equal((TerminalHost.DefaultCols, TerminalHost.DefaultRows), (_pty.Started[0].Request.Cols, _pty.Started[0].Request.Rows));
        Assert.Equal((20, 100), (_pty.Started[1].Request.Cols, _pty.Started[1].Request.Rows));
        Assert.Equal((300, 4), _pty.Started[1].Resizes[^1]);
        Assert.Equal((plain.Cols, plain.Rows), (TerminalHost.DefaultCols, TerminalHost.DefaultRows));
    }

    [Fact]
    public async Task It_runs_a_login_shell_or_one_command_in_it_and_a_blank_command_is_none()
    {
        // Pins: terminal#1
        await _host.StartAsync(new StartArgs(_folder, Shell: "/bin/bash", Command: "   ", Owner: "session-1"));
        await _host.StartAsync(new StartArgs(_folder, Shell: "/bin/bash", Command: "ls -la"));

        var shell = _pty.Started[0].Request;
        Assert.Equal("/bin/bash", shell.FileName);
        Assert.Equal(["-l"], shell.Arguments);
        Assert.Equal(_folder, shell.Cwd);
        Assert.Equal("xterm-256color", shell.Environment["TERM"]);
        Assert.Equal("truecolor", shell.Environment["COLORTERM"]);
        Assert.Equal("session-1", shell.Environment["ASSEMBLY_SESSION_ID"]);
        Assert.Equal("Assembly", shell.Environment["TERM_PROGRAM"]);
        Assert.True(shell.Environment.ContainsKey("NO_COLOR") && shell.Environment["NO_COLOR"] is null);
        Assert.Equal(["-l", "-c", "ls -la"], _pty.Started[1].Request.Arguments);
    }

    [Fact]
    public async Task Coming_back_catches_up_from_where_the_window_left_off_or_starts_from_the_end()
    {
        // Pins: terminal#5
        var terminal = await _host.StartAsync(new StartArgs(_folder));
        _pty.Started[0].Emit("one "u8.ToArray());
        _pty.Started[0].Emit("two "u8.ToArray());
        _pty.Started[0].Emit("three"u8.ToArray());
        await _topics.WaitFor(p => p.Topic == TerminalFeature.OutputTopic && Seq(p.Json) == 13);

        var (seq, rows) = _host.State(terminal.Id, after: 4);
        var (fresh, none) = _host.State(terminal.Id, after: null);

        Assert.Equal(4, seq);
        var row = Assert.Single(rows);
        Assert.Equal((13L, 4L), (Seq(row), PrevSeq(row)));
        Assert.Equal("two three"u8.ToArray(), Data(row));
        Assert.Equal(13, fresh);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Coming_back_after_the_exit_repeats_nothing_and_far_behind_reads_the_replay()
    {
        // Pins: terminal#5
        var terminal = await _host.StartAsync(new StartArgs(_folder));
        for (var i = 0; i < 16; i++)
            _pty.Started[0].Emit(new byte[64 * 1024]);
        _pty.Started[0].Emit(new byte[10]);
        _pty.Started[0].End(0);
        await _topics.WaitFor(p => p.Topic == TerminalFeature.OutputTopic && p.Json.Contains("\"exit\":"));
        var end = OutputRing.ReadLimit + 10L;

        var (seen, nothing) = _host.State(terminal.Id, after: end + 1);
        var (behind, onlyExit) = _host.State(terminal.Id, after: 0);

        Assert.Equal(end + 1, seen);
        Assert.Empty(nothing);
        Assert.Equal(end, behind);
        Assert.Equal((end + 1, end), (Seq(onlyExit.Single()), PrevSeq(onlyExit.Single())));
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

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private List<string> Rows(string terminal) =>
        [.. _topics.Snapshot().Where(p => p.Topic == TerminalFeature.OutputTopic && p.Key == terminal).Select(p => p.Json)];

    private static long Seq(string row) => JsonDocument.Parse(row).RootElement.GetProperty("seq").GetInt64();

    private static long PrevSeq(string row) => JsonDocument.Parse(row).RootElement.GetProperty("prevSeq").GetInt64();

    private static byte[] Data(string row) => JsonDocument.Parse(row).RootElement.GetProperty("data").GetBytesFromBase64();
}
