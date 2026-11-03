using System.Security.Cryptography;
using System.Text;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>Drains both pipes after the retained output limit; the descriptor reports every observed byte.</summary>
internal sealed class CommandOutput(string path) : IAsyncDisposable
{
    public const int Limit = 16 * 1024 * 1024;
    private static readonly byte[] Marker = "\n[Assembly: command output truncated]\n"u8.ToArray();
    private readonly FileStream _file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, true);
    private readonly SemaphoreSlim _write = new(1);
    private long _observed;
    private long _retained;

    public async Task DrainAsync(Stream source)
    {
        var buffer = new byte[65536];
        while (await source.ReadAsync(buffer) is var count && count != 0)
        {
            await _write.WaitAsync();
            try
            {
                _observed = checked(_observed + count);
                var keep = (int)Math.Min(count, Limit - _retained);
                if (keep > 0) { await _file.WriteAsync(buffer.AsMemory(0, keep)); _retained += keep; }
            }
            finally { _write.Release(); }
        }
    }

    public async Task<(string Hash, long Bytes, long Observed, bool Truncated, string Summary)> FinishAsync()
    {
        var truncated = _observed > Limit;
        if (truncated)
        {
            _file.Position = Limit - Marker.Length;
            await _file.WriteAsync(Marker);
        }
        await _file.FlushAsync(); _file.Flush(true); _file.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(_file));
        // Reading the bounded tail cannot turn a bulk receipt into a live-socket payload.
        _file.Position = Math.Max(0, _file.Length - 32768);
        using var reader = new StreamReader(_file, Encoding.UTF8, true, 4096, true);
        var tail = await reader.ReadToEndAsync();
        var summary = string.Join('\n', tail.Split('\n').TakeLast(200));
        while (System.Text.Json.JsonEncodedText.Encode(summary).EncodedUtf8Bytes.Length > 8192) summary = summary[(summary.Length / 4)..];
        return (hash, _file.Length, _observed, truncated, summary);
    }

    public async ValueTask DisposeAsync() { await _file.DisposeAsync(); _write.Dispose(); }
}
