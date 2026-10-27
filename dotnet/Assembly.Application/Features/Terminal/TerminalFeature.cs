using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// The terminal calls (<c>terminal/start</c>, <c>list</c>, <c>write</c>, <c>resize</c>, <c>read</c>, <c>close</c>) and
/// the <c>terminal</c> rows topic: one terminal's output, keyed by its id, with byte offsets as seqs and its exit as
/// the last row. Every machine serves it. Reads run side by side, so the window keeps one <c>write</c> in flight per
/// terminal and sends what's typed meanwhile in the next. See <c>dotnet/notes/terminal-shape.md</c>.
/// </summary>
internal sealed class TerminalFeature : IFeature
{
    public const string OutputTopic = "terminal";

    private readonly TerminalHost _host;

    public TerminalFeature(TerminalHost host) => _host = host;

    public void Register(ICallRegistry calls)
    {
        var json = TerminalJson.Default;
        calls.Read("terminal", "start", json.StartArgs, json.TerminalInfo, (args, _) => Task.FromResult(_host.Start(args)));
        calls.Read("terminal", "list", json.Empty, json.IReadOnlyListTerminalInfo, (_, _) => Task.FromResult(_host.List()));
        calls.Read("terminal", "write", json.WriteArgs, json.Empty, async (args, call) =>
        {
            await _host.WriteAsync(args.Terminal, args.Data, call.Cancellation);
            return new Empty();
        });
        calls.Read("terminal", "resize", json.ResizeArgs, json.Empty, (args, _) =>
        {
            _host.Resize(args.Terminal, args.Cols, args.Rows);
            return Task.FromResult(new Empty());
        });
        calls.Read("terminal", "read", json.ReadArgs, json.OutputPiece, (args, _) => Task.FromResult(_host.Read(args.Terminal, args.Offset)));
        calls.Read("terminal", "close", json.TerminalKey, json.Empty, async (args, _) =>
        {
            await _host.CloseAsync(args.Terminal);
            return new Empty();
        });
        calls.Rows(OutputTopic, (terminal, after, _) => _host.State(terminal, after));
    }
}
