using System.Text;
using Assembly.Application.Common.Calls;

namespace Assembly.Remote.Cli;

/// <summary>One persistent private installation identity beneath this host's actual locked home.</summary>
internal static class RemoteCliOwnerIdentity
{
    internal static async Task<string> ReadOrCreateAsync(string home, CancellationToken ct)
    {
        var path = Path.Combine(home, "cli-installation-id");
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, ct);
            if (!Guid.TryParseExact(existing, "N", out _)) throw new CallFailed("configuration_unavailable");
            return existing;
        }
        var identity = Guid.NewGuid().ToString("N");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
                await file.WriteAsync(Encoding.UTF8.GetBytes(identity), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, false);
            return identity;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
