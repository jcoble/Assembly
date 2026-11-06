using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;
using Assembly.Application.Common.Terminals;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// This machine's terminals, by id. Terminals outlive the sockets that use them (terminal#5); disposing the host, when
/// the backend stops, closes every one (terminal#8).
/// </summary>
internal sealed class TerminalHost : ITerminals, IProgramTerminals, IAsyncDisposable
{
    public const int DefaultCols = 96;
    public const int DefaultRows = 28;

    private readonly IPtyStarter _pty;
    private readonly ITopics _topics;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _sessionFolder;
    private readonly (string? Path, string? Error) _loginShell;
    private readonly Dictionary<string, Task> _closing = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RunningTerminal> _terminals = [];
    private bool _stopped;

    /// <param name="sessionFolder">A session's folder, once it exists, for a start with an owner and no folder. It throws for a session this machine doesn't run, which fails the start.</param>
    public TerminalHost(IPtyStarter pty, ITopics topics, TimeProvider time, (string? Path, string? Error) loginShell, Func<string, string?>? sessionFolder = null)
    {
        _pty = pty;
        _topics = topics;
        _time = time;
        _loginShell = loginShell;
        _sessionFolder = sessionFolder ?? (_ => null);
    }

    /// <summary>
    /// Starts a terminal in <see cref="StartArgs.Cwd"/>, or answers the owner's terminal while it still runs
    /// (terminal#9), closing its shell that has ended. Fails with the folder's path when it doesn't exist (terminal#1). With no folder it starts in the
    /// owner's existing session folder. Opening Terminal never materializes a checkout or substitutes a home folder.
    /// </summary>
    public async Task<TerminalInfo> StartAsync(StartArgs args, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_lock) if (_stopped) throw new CallFailed("the backend is stopping");
        var owner = Blank(args.Owner);
        var command = Blank(args.Command);
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
        // Asked whatever the folder, since it refuses a session this machine doesn't run.
        var sessionFolder = owner is null ? null : Blank(_sessionFolder(owner));
        var requested = Blank(args.Cwd);
        if (owner is not null && sessionFolder is null)
            throw new CallFailed($"The terminal folder for session {owner} is not ready.");
        if (owner is not null && requested is not null && Path.GetFullPath(requested) != Path.GetFullPath(sessionFolder!))
            throw new CallFailed($"The terminal folder for session {owner} is {sessionFolder}, not {requested}.");
        var cwd = sessionFolder ?? requested ?? throw new CallFailed("Choose an existing folder to start a terminal.");
        // Only a session's shell is handed back; a command it runs (a Run, a sign-in) is a terminal of its own.
        var reusable = owner is not null && command is null;
        if (reusable && RunningShell(owner) is { } running)
            return running;
        // The session's shell has ended, and it gets a new one: the old one's output goes, so ended shells don't pile up.
        if (reusable)
            foreach (var ended in EndedShells(owner))
                await CloseAsync(ended.Info.Id, cancellation);
        var shell = Blank(args.Shell);
        if (shell is null)
        {
            if (_loginShell.Error is not null || Blank(_loginShell.Path) is not { } account || !Path.IsPathFullyQualified(account))
                throw new CallFailed(_loginShell.Error ?? "the configured account login shell is unavailable");
            shell = account;
        }
        cancellation.ThrowIfCancellationRequested();
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

    public async Task<ProgramTerminal> StartProgramAsync(ProgramStart start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IPty pty;
        try
        {
            pty = _pty.Start(new PtyRequest(start.File, start.Args, start.Cwd, start.Env, start.Cols, start.Rows, ReplaceEnvironment: true));
        }
        catch (Exception) when (start.Owner.StartsWith("signin:", StringComparison.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            throw new CallFailed("signin_start_failed", "signin_start_failed");
        }
        var info = new TerminalInfo($"term-{Guid.NewGuid():N}", start.Cwd, start.File, start.File,
            start.Cols, start.Rows, pty.Pid, start.Owner, _time.GetUtcNow(), null);
        lock (_lock)
        {
            if (!_stopped && !ct.IsCancellationRequested)
            {
                _terminals[info.Id] = new RunningTerminal(info, pty, _topics);
                return new ProgramTerminal(info.Id, info.Pid);
            }
        }
        try { await pty.DisposeAsync(); }
        catch (Exception) when (start.Owner.StartsWith("signin:", StringComparison.Ordinal))
        {
            throw new CallFailed("signin_exit_failed", "signin_exit_failed");
        }
        ct.ThrowIfCancellationRequested();
        throw new CallFailed("signin_start_failed", "signin_start_failed");
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> OutputAsync(string terminal, CancellationToken ct) =>
        Get(terminal).OutputAsync(ct);

    public Task<int> ExitedAsync(string terminal, CancellationToken ct) => Get(terminal).ExitedAsync(ct);

    ValueTask IProgramTerminals.WriteAsync(string terminal, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        Get(terminal).WriteAsync(data, ct);

    public Task<IReadOnlyList<TerminalInfo>> ListAsync(string? owner, CancellationToken cancellation) =>
        Task.FromResult<IReadOnlyList<TerminalInfo>>(List().Where(info => owner is null || info.Owner == owner).ToArray());

    public Task ResizeAsync(string terminal, int cols, int rows, CancellationToken cancellation)
    {
        Resize(terminal, cols, rows);
        return Task.CompletedTask;
    }

    public Task<OutputPiece> ReadAsync(string terminal, long offset, CancellationToken cancellation) => Task.FromResult(Read(terminal, offset));

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

    /// <summary>Ends the terminal and lets go of its output; ok for one already closed.</summary>
    public async ValueTask CloseAsync(string terminal, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Task close;
        TaskCompletionSource? ready = null;
        lock (_lock)
        {
            if (!_terminals.TryGetValue(terminal, out var running)) return;
            if (!_closing.TryGetValue(terminal, out close!))
            {
                ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                close = CloseCaptured(terminal, running, ready.Task);
                _closing.Add(terminal, close);
            }
        }
        ready?.TrySetResult();
        // Once disposal starts, its exact process/reader remains owned through completion.
        await close;
    }

    private async Task CloseCaptured(string id, RunningTerminal running, Task ready)
    {
        await ready;
        try
        {
            await running.DisposeAsync();
            lock (_lock)
                if (_terminals.TryGetValue(id, out var current) && ReferenceEquals(current, running)) _terminals.Remove(id);
        }
        finally { lock (_lock) _closing.Remove(id); }
    }

    public async ValueTask CloseOwnerAsync(string owner, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string[] ids;
        lock (_lock) ids = _terminals.Values.Where(item => item.Info.Owner == owner && item.Info.Command is null).Select(item => item.Info.Id).ToArray();
        foreach (var id in ids) await CloseAsync(id, cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (_lock) { _stopped = true; ids = _terminals.Keys.ToArray(); }
        List<Exception> errors = [];
        foreach (var id in ids)
            try { await CloseAsync(id); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Some terminal processes could not be closed.", errors);
    }

    private RunningTerminal Get(string terminal)
    {
        lock (_lock)
            return _terminals.TryGetValue(terminal, out var running) ? running : throw new CallFailed($"there's no terminal {terminal}", "terminal_missing");
    }

    private List<RunningTerminal> EndedShells(string? owner)
    {
        lock (_lock)
        {
            var ended = _terminals.Values.Where(terminal => terminal.Info is { Command: null, ExitCode: not null } info && info.Owner == owner).ToList();
            return ended;
        }
    }

    private TerminalInfo? RunningShell(string? owner)
    {
        lock (_lock)
            return _terminals.Values.Select(terminal => terminal.Info).FirstOrDefault(info => info.Owner == owner && info.Command is null && info.ExitCode is null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
