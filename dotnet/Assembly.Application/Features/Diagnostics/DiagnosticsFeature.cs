using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Diagnostics;

/// <summary>
/// The Mac page's own calls (plan B8), named as the page named them: <c>diagnostics/memory.sample</c>,
/// <c>activity.begin</c> and <c>activity.end</c> around a turn (App Nap), <c>drop</c> for the debug control that drops
/// every subscription, and <c>soak.append</c>.
/// </summary>
internal sealed class DiagnosticsFeature : IFeature
{
    private readonly IDiagnosticsHost _host;
    private readonly Action _dropSubscriptions;

    public DiagnosticsFeature(IDiagnosticsHost host, Action dropSubscriptions)
    {
        _host = host;
        _dropSubscriptions = dropSubscriptions;
    }

    public void Register(ICallRegistry calls)
    {
    }
}
