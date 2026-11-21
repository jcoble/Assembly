using Assembly.Infrastructure.Remote.Copies;

namespace Assembly.Infrastructure.Remote;

/// <summary>The saved copies of this machine's sessions (TSK-1692): their download, on the bulk socket.</summary>
internal sealed partial class RemoteMachine
{
    private RemoteDownload? _download;

    private RemoteDownload Download => _download ?? throw new ObjectDisposedException(nameof(RemoteMachine));

    /// <summary>Starts or joins the session's download; returns once its newest screen is saved here.</summary>
    internal Task EnsureCopyAsync(string session, CancellationToken ct) => Download.Ensure(session, ct);

    private RemoteDownload StartDownloads() =>
        new(_hub, (method, args, ct) => CallAsync("rows", method, args, true, ct), VerifiedEngineAsync, AddSubagentSession,
            session => _follower.Watch(session), _follower.ForgetAsync, _log);

    // The engine id this socket verified. Until the mirror's handshake verifies it, this waits on the identity the
    // session publication awaits, and asks the remote nothing itself.
    private async Task<string> VerifiedEngineAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _engineIdentity) is { } known) return known;
        var owner = Volatile.Read(ref _sessionPublication) ?? throw Unreachable("not connected", $"{_name} is not connected");
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct, owner.Stop.Token);
        return await owner.Identity.Task.WaitAsync(connection.Token);
    }
}
