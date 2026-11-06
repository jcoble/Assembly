using System.Text.Json;
using System.Runtime.InteropServices;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Remote.Cli;

/// <summary>A fixed read helper. The private capture filename is supplied only by the owning installation binding.</summary>
internal static class CliReadRun
{
    internal static async Task<int> MainAsync(IReadOnlyList<string> args)
    {
        if (args is not ["--capture", var captureFile] || !Path.IsPathFullyQualified(captureFile)) return 2;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var bytes = await ReadInput(deadline, request: true);
            var request = JsonSerializer.Deserialize(bytes, UpdatesJson.Default.CliCheckArgs);
            if (request is null) return 2;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var lifetime = ReadInput(stop, request: false);
            try
            {
                var capture = RemoteCliCapture.Load(captureFile);
                var result = await new RemoteCliCheck(TimeProvider.System).ReadAsync(capture, request, stop.Token);
                var response = JsonSerializer.SerializeToUtf8Bytes(result, RemoteCliJson.Default.RemoteCliReadResult);
                if (response.Length > 64 * 1024) return 1;
                await Console.OpenStandardOutput().WriteAsync(response, stop.Token);
                return 0;
            }
            finally { await stop.CancelAsync(); await lifetime; }
        }
        catch (Exception error) when (error is CallFailed or IOException or UnauthorizedAccessException or JsonException or
            OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // No raw exception, private source/configuration or process output leaves this helper.
            Console.Error.WriteLine("CLI check unavailable.");
            return 1;
        }
    }

    // Console stdin is a synchronous pipe on Unix; cancelling Stream.ReadAsync does not reliably unblock it.
    // poll waits for either SSH lifetime EOF or our owned cancellation pipe, with no repeated timer.
    private static async Task<byte[]> ReadInput(CancellationTokenSource stop, bool request)
    {
        var descriptors = new int[2];
        if (pipe(descriptors) != 0) throw new IOException("Input unavailable.");
        try
        {
            using var cancellation = stop.Token.Register(() => write(descriptors[1], [1], 1));
            return await Task.Run(() =>
            {
                var fds = new[] { new PollFd { File = 0, Events = 1 }, new PollFd { File = descriptors[0], Events = 1 } };
                var bytes = new byte[4097]; var length = 0; var value = new byte[1];
                while (true)
                {
                    while (poll(fds, 2, -1) < 0)
                        if (Marshal.GetLastPInvokeError() != 4) throw new IOException("Input unavailable.");
                    if (stop.IsCancellationRequested)
                    {
                        if (request) stop.Token.ThrowIfCancellationRequested();
                        return [];
                    }
                    if (!request) { stop.Cancel(); return []; }
                    if (read(0, value, 1) != 1) throw new IOException("Input unavailable.");
                    bytes[length++] = value[0];
                    if (value[0] == (byte)'\n') return bytes[..length];
                    if (length == bytes.Length) throw new IOException("Input unavailable.");
                }
            });
        }
        finally { close(descriptors[0]); close(descriptors[1]); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int File; public short Events; public short Returned; }
    [DllImport("libc", SetLastError = true)] private static extern int pipe([Out] int[] descriptors);
    [DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] descriptors, nuint count, int timeout);
    [DllImport("libc")] private static extern nint read(int descriptor, byte[] buffer, nuint count);
    [DllImport("libc")] private static extern nint write(int descriptor, byte[] buffer, nuint count);
    [DllImport("libc")] private static extern int close(int descriptor);
}
