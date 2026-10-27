using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// The terminal calls (<c>terminal/start</c>, <c>write</c>, <c>resize</c>, <c>read</c>, <c>close</c>), the
/// <c>terminals</c> topic (every terminal on this machine, upserted on start and exit, removed on close) and the
/// <c>terminal</c> rows topic (one terminal's output, keyed by its id, with byte offsets as seqs). Every machine
/// serves it. See <c>dotnet/notes/terminal-shape.md</c>.
/// </summary>
internal sealed class TerminalFeature : IFeature
{
    public const string ListTopic = "terminals";
    public const string OutputTopic = "terminal";

    public TerminalFeature(TerminalHost host) => throw new NotImplementedException();

    public void Register(ICallRegistry calls) => throw new NotImplementedException();
}
