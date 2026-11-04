using System.Text.Json;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

/// <summary>Only app-dev names this file. Atomic replacement pushes how its launch update is going, even when the host is down.</summary>
internal sealed class DevRemoteSyncStatus : IDisposable
{
    internal const string Variable = "ASSEMBLY_DEV_REMOTE_SYNC_STATUS";
    private const string Machine = "workbox-test";
    private readonly FileSystemWatcher _watcher;
    private readonly MachineStatus _status;
    private readonly string _path;
    private readonly Action _updated;
    private readonly Lock _gate = new();
    private bool _updating;
    private bool _disposed;

    internal DevRemoteSyncStatus(string path, MachineStatus status, Action updated)
    {
        _path = path; _status = status; _updated = updated;
        _watcher = new(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        _watcher.Changed += (_, _) => Read();
        _watcher.Created += (_, _) => Read();
        _watcher.Renamed += (_, _) => Read();
        _watcher.EnableRaisingEvents = true;
        Read();
    }

    private void Read()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(_path));
                var updating = json.RootElement.TryGetProperty("updating", out var running) && running.ValueKind == JsonValueKind.True;
                var reason = json.RootElement.TryGetProperty("reason", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                _status.Updating(Machine, updating, reason);
                if (_updating && !updating && reason is null) _updated();
                _updating = updating;
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _watcher.Dispose(); }
    }
}
