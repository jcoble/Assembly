using Assembly.Application.Features.Terminal;

namespace Assembly.Tests.Draft.Terminal;

internal sealed class FakePtyStarter : IPtyStarter
{
    public List<FakePty> Started { get; } = [];

    public Action? BeforeStart { get; set; }

    public IPty Start(PtyRequest request)
    {
        BeforeStart?.Invoke();
        var pty = new FakePty(request, 1000 + Started.Count);
        Started.Add(pty);
        return pty;
    }
}
