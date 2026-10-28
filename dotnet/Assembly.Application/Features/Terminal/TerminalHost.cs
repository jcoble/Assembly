using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// This machine's terminals, by id. Terminals outlive the sockets that use them (terminal#5); disposing the host, when
/// the backend stops, closes every one (terminal#8).
/// </summary>
internal sealed class TerminalHost : IAsyncDisposable
{
    public const int DefaultCols = 96;
    public const int DefaultRows = 28;

    private readonly IPtyStarter _pty;
    private readonly ITopics _topics;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _sessionFolder;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RunningTerminal> _terminals = [];
    private bool _stopped;

    /// <param name="sessionFolder">A session's folder, once it exists, for a start with an owner and no folder.</param>
    public TerminalHost(IPtyStarter pty, ITopics topics, TimeProvider time, Func<string, string?>? sessionFolder = null)
    {
        _pty = pty;
        _topics = topics;
        _time = time;
        _sessionFolder = sessionFolder ?? (_ => null);
    }

    /// <summary>
    /// Starts a terminal in <see cref="StartArgs.Cwd"/>, or answers the owner's terminal while it still runs
    /// (terminal#9). Fails with the folder's path when it doesn't exist (terminal#1). With no folder it starts in the
    /// owner's session folder, or the home folder while that session has none yet (its first turn checks it out).
    /// </summary>
    public async Task<TerminalInfo> StartAsync(StartArgs args)
    {
        var owner = Blank(args.Owner);
        var command = Blank(args.Command);
        var shell = Blank(args.Shell) ?? Blank(Environment.GetEnvironmentVariable("SHELL")) ?? "/bin/sh";
        // A login shell, so the terminal has the user's whole PATH, as Terminal.app and the old app gave it.
        string[] arguments = command is not null ? ["-l", "-c", command] : ["-l"];
        var cols = Math.Clamp(args.Cols ?? DefaultCols, 20, 300);
        var rows = Math.Clamp(args.Rows ?? DefaultRows, 4, 100);
        var environment = new Dictionary<string, string?>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "Assembly",
            ["CLICOLOR"] = "1",
            ["CLICOLOR_FORCE"] = "1",
            ["FORCE_COLOR"] = "3",
            ["COLORFGBG"] = "15;0",
            ["NO_COLOR"] = null,
        };
        if (owner is not null)
            environment["ASSEMBLY_SESSION_ID"] = owner;
        var cwd = Blank(args.Cwd) ?? (owner is null ? null : _sessionFolder(owner))
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Only a session's shell is handed back; a command it runs (a Run, a sign-in) is a terminal of its own.
        var reusable = owner is not null && command is null;
        if (reusable && RunningShell(owner) is { } running)
            return running;
        // Started outside the lock: a subscriber's state takes it under the Topics lock, so a slow start (a folder on
        // a sleeping drive) would hold up every publish.
        var pty = _pty.Start(new PtyRequest(shell, arguments, cwd, environment, cols, rows));
        var info = new TerminalInfo($"term-{Guid.NewGuid():N}", cwd, shell, command, cols, rows, pty.Pid, owner, _time.GetUtcNow(), null);
        TerminalInfo? raced;
        bool stopped;
        lock (_lock)
        {
            // A start that was already running when the host stopped: nobody would close what it opened.
            stopped = _stopped;
            raced = reusable ? RunningShell(owner) : null;
            if (!stopped && raced is null)
            {
                _terminals[info.Id] = new RunningTerminal(info, pty, _topics);
                return info;
            }
        }
        await pty.DisposeAsync();
        return stopped ? throw new CallFailed("the backend is stopping") : raced!;
    }

    public IReadOnlyList<TerminalInfo> List()
    {
        lock (_lock)
            return [.. _terminals.Values.Select(terminal => terminal.Info)];
    }

    /// <summary>Fails for a terminal that's closed or whose process has ended.</summary>
    public ValueTask WriteAsync(string terminal, byte[] data, CancellationToken cancellationToken) =>
        Get(terminal).WriteAsync(data, cancellationToken);

    public void Resize(string terminal, int cols, int rows) => Get(terminal).Resize(Math.Clamp(cols, 20, 300), Math.Clamp(rows, 4, 100));

    public OutputPiece Read(string terminal, long offset) => Get(terminal).Read(offset);

    /// <summary>Runs under the Topics lock: it takes the host's lock, then the terminal's, and never publishes.</summary>
    public (long Seq, IReadOnlyList<string> Rows) State(string terminal, long? after)
    {
        lock (_lock)
            return _terminals.TryGetValue(terminal, out var running) ? running.State(after) : (after ?? 0, []);
    }

    /// <summary>Ends the terminal and lets go of its output; ok for one already closed.</summary>
    public async ValueTask CloseAsync(string terminal)
    {
        RunningTerminal? running;
        lock (_lock)
            _terminals.Remove(terminal, out running);
        if (running is not null)
            await running.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        List<RunningTerminal> all;
        lock (_lock)
        {
            _stopped = true;
            all = [.. _terminals.Values];
            _terminals.Clear();
        }
        foreach (var running in all)
            await running.DisposeAsync();
    }

    private RunningTerminal Get(string terminal)
    {
        lock (_lock)
            return _terminals.TryGetValue(terminal, out var running) ? running : throw new CallFailed($"there's no terminal {terminal}");
    }

    private TerminalInfo? RunningShell(string? owner)
    {
        lock (_lock)
            return _terminals.Values.Select(terminal => terminal.Info).FirstOrDefault(info => info.Owner == owner && info.Command is null && info.ExitCode is null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
