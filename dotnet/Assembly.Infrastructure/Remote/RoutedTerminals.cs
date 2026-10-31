using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Terminal;

namespace Assembly.Infrastructure.Remote;

/// <summary>Routes by the session's owner on start/list, and by the machine-qualified id afterward.</summary>
internal sealed class RoutedTerminals(TerminalHost local, SessionStore sessions, MachineSet machines) : ITerminals
{
    private static TerminalJson Json => TerminalJson.Default;

    public async Task<TerminalInfo> StartAsync(StartArgs args, CancellationToken cancellation = default)
    {
        if (args.Owner is not { } owner || sessions.Get(owner)?.Machine is not { } machine)
            return await local.StartAsync(args, cancellation);
        var remote = Remote(machine);
        var info = await remote.CallAsync("start", args, Json.StartArgs, Json.TerminalInfo, cancellation);
        await remote.FollowAsync(info.Id, cancellation);
        return info with { Id = remote.Qualify(info.Id) };
    }

    public async Task<IReadOnlyList<TerminalInfo>> ListAsync(string? owner, CancellationToken cancellation)
    {
        if (owner is not null && sessions.Get(owner)?.Machine is not { })
            return await local.ListAsync(owner, cancellation);
        var all = owner is null ? local.List().ToList() : new List<TerminalInfo>();
        var names = owner is not null ? new[] { sessions.Get(owner)!.Machine! } : machines.Remotes.Select(machine => machine.Name!).ToArray();
        foreach (var name in names)
        {
            var remote = Remote(name);
            var listed = await remote.CallAsync("list", new ListArgs(owner), Json.ListArgs, Json.IReadOnlyListTerminalInfo, cancellation);
            foreach (var info in listed)
            {
                if (info.ExitCode is null) await remote.FollowAsync(info.Id, cancellation);
                else await remote.UnfollowAsync(info.Id, cancellation);
                all.Add(info with { Id = remote.Qualify(info.Id) });
            }
        }
        return all;
    }

    public async ValueTask WriteAsync(string terminal, byte[] data, CancellationToken cancellation)
    {
        if (Split(terminal) is not { } remote) { await local.WriteAsync(terminal, data, cancellation); return; }
        await remote.Socket.CallAsync("write", new WriteArgs(remote.Id, data), Json.WriteArgs, Json.Empty, cancellation);
    }

    public async Task ResizeAsync(string terminal, int cols, int rows, CancellationToken cancellation)
    {
        if (Split(terminal) is not { } remote) { await local.ResizeAsync(terminal, cols, rows, cancellation); return; }
        await remote.Socket.CallAsync("resize", new ResizeArgs(remote.Id, cols, rows), Json.ResizeArgs, Json.Empty, cancellation);
    }

    public Task<OutputPiece> ReadAsync(string terminal, long offset, CancellationToken cancellation) => Split(terminal) is { } remote
        ? remote.Socket.CallAsync("read", new ReadArgs(remote.Id, offset), Json.ReadArgs, Json.OutputPiece, cancellation)
        : local.ReadAsync(terminal, offset, cancellation);

    public async ValueTask CloseAsync(string terminal, CancellationToken cancellation = default)
    {
        if (Split(terminal) is not { } remote) { await local.CloseAsync(terminal, cancellation); return; }
        await remote.Socket.CallAsync("close", new TerminalKey(remote.Id), Json.TerminalKey, Json.Empty, cancellation);
        await remote.Socket.UnfollowAsync(remote.Id, cancellation);
    }

    private RemoteTerminalSocket Remote(string name) => machines.Named(name) is RemoteMachine machine
        ? machine.Terminals : throw new MachineUnreachable(name, $"{name} isn't connected here");

    private (RemoteTerminalSocket Socket, string Id)? Split(string id)
    {
        const string prefix = "assembly-remote-terminal://";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = id[prefix.Length..].Split('/', 2);
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty)) throw new CallFailed("invalid remote terminal id");
        return (Remote(Uri.UnescapeDataString(parts[0])), Uri.UnescapeDataString(parts[1]));
    }
}
