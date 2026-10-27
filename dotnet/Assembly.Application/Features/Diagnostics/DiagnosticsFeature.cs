using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Diagnostics;

/// <summary>
/// The Mac page's own calls (plan B8), named as the page named them: <c>diagnostics/memory.sample</c>,
/// <c>activity.begin</c> and <c>activity.end</c> around a turn (App Nap), <c>drop</c> for the debug control that drops
/// every subscription and every open socket (the caller's too, so its answer may not arrive), and <c>soak.append</c>.
/// </summary>
internal sealed class DiagnosticsFeature : IFeature
{
    private readonly IDiagnosticsHost _host;
    private readonly Action _drop;

    public DiagnosticsFeature(IDiagnosticsHost host, Action drop)
    {
        _host = host;
        _drop = drop;
    }

    public void Register(ICallRegistry calls)
    {
        var empty = DiagnosticsJson.Default.Empty;
        calls.Read("diagnostics", "memory.sample", empty, DiagnosticsJson.Default.MemoryReading, (_, _) => Task.FromResult(_host.Memory()));
        calls.Read("diagnostics", "activity.begin", empty, empty, (_, _) => Done(_host.BeginActivity));
        calls.Read("diagnostics", "activity.end", empty, empty, (_, _) => Done(_host.EndActivity));
        calls.Read("diagnostics", "drop", empty, empty, (_, _) => Done(_drop));
        calls.Read("diagnostics", "soak.append", DiagnosticsJson.Default.SoakLine, empty, (soak, _) =>
        {
            if (soak.Name is not { Length: > 0 } name || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || soak.Line is null)
                throw new CallFailed("soak.append needs { name, line } with a plain file name");
            return Done(() => _host.AppendSoak(soak.Name, soak.Line));
        });
    }

    private static Task<Empty> Done(Action action)
    {
        action();
        return Task.FromResult(new Empty());
    }
}
