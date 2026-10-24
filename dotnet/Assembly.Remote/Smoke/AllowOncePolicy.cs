using Assembly.Engine.Acp;
using Assembly.Engine.Acp.Messages;

namespace Assembly.Remote.Smoke;

/// <summary>Stands in for the user: picks "allow once" and remembers what it was asked.</summary>
public sealed class AllowOncePolicy : IPermissionPolicy
{
    private readonly Lock _gate = new();
    private readonly List<string> _asked = [];

    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_gate)
                return [.. _asked];
        }
    }

    public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
            _asked.Add(request.ToolTitle);

        var option = request.Options.FirstOrDefault(o => o.Kind == "allow_once")
            ?? request.Options.FirstOrDefault(o => o.Kind.StartsWith("allow", StringComparison.Ordinal));
        return Task.FromResult(option is null ? PermissionOutcome.Cancelled : PermissionOutcome.Selected(option.OptionId));
    }
}
