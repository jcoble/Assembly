using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// The terminal calls (<c>terminal/start</c>, <c>list</c>, <c>write</c>, <c>resize</c>, <c>read</c>, <c>close</c>) and
/// the <c>terminal</c> change topic on the terminal socket: one terminal's output, keyed by its id, with byte offsets as seqs and its exit as
/// the last row. Every machine serves it. Reads run side by side, so the window keeps one <c>write</c> in flight per
/// terminal and sends what's typed meanwhile in the next.
/// </summary>
internal sealed class TerminalFeature : IFeature
{
    public const string OutputTopic = "terminal";

    private readonly ITerminals _host;

    public TerminalFeature(ITerminals host) => _host = host;

    public void Register(ICallRegistry calls)
    {
        var json = TerminalJson.Default;
        calls.Read("terminal", "start", json.StartArgs, json.TerminalInfo, (args, call) => _host.StartAsync(args, call.Cancellation));
        calls.Read("terminal", "list", json.ListArgs, json.IReadOnlyListTerminalInfo, (args, call) => _host.ListAsync(args.Owner, call.Cancellation));
        calls.Read("terminal", "write", json.WriteArgs, json.Empty, async (args, call) =>
        {
            await _host.WriteAsync(args.Terminal, args.Data, call.Cancellation);
            return new Empty();
        });
        calls.Read("terminal", "resize", json.ResizeArgs, json.Empty, async (args, call) =>
        {
            await _host.ResizeAsync(args.Terminal, args.Cols, args.Rows, call.Cancellation);
            return new Empty();
        });
        calls.Read("terminal", "read", json.ReadArgs, json.OutputPiece, (args, call) => _host.ReadAsync(args.Terminal, args.Offset, call.Cancellation));
        calls.Read("terminal", "close", json.TerminalKey, json.Empty, async (args, call) =>
        {
            await _host.CloseAsync(args.Terminal, call.Cancellation);
            return new Empty();
        });
        // State is always empty: catch-up is terminal/read. Each change carries its published key, the terminal id.
        calls.Topic(OutputTopic, json.OutputRow, key: _ => "", state: (_, _) => [], terminal: true);
    }
}
