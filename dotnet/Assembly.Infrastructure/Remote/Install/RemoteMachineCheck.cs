using System.Globalization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>
/// What's on a machine before anything is put there. An install has one form: this folder and this service. Anything
/// else in those places is somebody's, so it stops here, by name, with nothing changed; there's no going ahead anyway.
/// Updating tools needs an install of that form to be there already, since a host this installer didn't make isn't its to change.
/// </summary>
internal static class RemoteMachineCheck
{
    internal static string Command(int port) => """
        root=$HOME/assembly-host; unit=$HOME/.config/systemd/user/assembly-host.service
        if [ ! -e "$root" ] || [ -z "$(ls -A "$root" 2>/dev/null)" ]; then folder=fresh
        elif [ -x "$root/bin/assembly-remote-server" ] && grep -q '"packageType": "assembly-remote-backend"' "$root/serve/backend-manifest.json" 2>/dev/null; then folder=ours
        else folder=other; fi
        if [ ! -e "$unit" ]; then service=none
        elif grep -q "^ExecStart=$root/bin/assembly-remote-server serve " "$unit"; then service=ours
        else service=other; fi
        listener=$(ss -ltnpH "sport = :PORT" | head -n 1)
        holder=free
        if [ -n "$listener" ]; then
          pid=$(printf '%s' "$listener" | sed -n 's/.*pid=\([0-9]*\).*/\1/p')
          if [ -n "$pid" ] && [ "$pid" = "$(systemctl --user show assembly-host.service -p MainPID --value 2>/dev/null)" ]; then holder=ours
          else
            holder=$(sed -n 's#.*/\([^/]*\.service\).*#\1#p' "/proc/$pid/cgroup" 2>/dev/null | tail -n 1)
            [ -n "$holder" ] || holder=$(printf '%s' "$listener" | sed -n 's/.*users:(("\([^"]*\)".*/\1/p')
            [ -n "$holder" ] || holder=another
          fi
        fi
        printf '%s\t%s\t%s\n' "$folder" "$service" "$holder"
        """.Replace("PORT", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    internal static void Require(string said, SavedMachine machine, bool toolsOnly)
    {
        var parts = said.Split('\t');
        if (parts.Length != 3) throw new CallFailed($"Can't tell what's on {machine.Name}, so nothing was changed: {said}", "remote_invalid");
        var holder = parts[2];
        if (holder is not ("free" or "ours"))
            throw new CallFailed($"Port {machine.Port} on {machine.Name} is in use by {(holder == "another" ? "a program this account can't see" : holder)}, so nothing was changed. Change the port in Settings, or free it there.", "port_taken");
        var what = parts[0] == "other" ? "The folder ~/assembly-host" : parts[1] == "other" ? "The service assembly-host.service" : null;
        if (what is not null)
            throw new CallFailed($"{what} on {machine.Name} isn't an install this app made, so nothing was changed. Assembly won't write there: move it aside yourself if it's not needed.", "install_unrecognized");
        if (toolsOnly && parts[0] != "ours")
            throw new CallFailed($"The backend on {machine.Name} isn't one this app installed, so its adapters and CLIs weren't changed.", "install_unrecognized");
    }
}
