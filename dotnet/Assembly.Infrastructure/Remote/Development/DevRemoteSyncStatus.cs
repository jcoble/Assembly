using System.Text.Json;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Remote.Development;

/// <summary>Only app-dev supplies this path. Atomic replacement pushes its owned sync result, even when the host is down.</summary>
internal sealed class DevRemoteSyncStatus : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly MachineStatus _status;
    private readonly string _path;
    private readonly Lock _gate = new();
    private bool _disposed;

    internal DevRemoteSyncStatus(string path, MachineStatus status)
    {
        _path = path; _status = status;
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
                if (!json.RootElement.TryGetProperty("reason", out var reason)) return;
                _status.Deployment("workbox-test", reason.ValueKind == JsonValueKind.String ? reason.GetString() : null);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _watcher.Dispose(); }
    }
}
