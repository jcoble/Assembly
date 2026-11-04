using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed record RemoteInstallStaged(int Pid, string UnitSha256);

internal class RemoteInstallProcess(TimeProvider time)
{
    internal virtual async Task<RemoteInstallStaged> StageAsync(RemoteInstallStart request, RemoteInstallBinding binding, RemoteInstallPackage.Checked package, string snapshotFile,
        Action<long> progress, CancellationToken ct)
    {
        RemoteInstallTarget.Require(binding, ct);
        await using var input = new FileStream(snapshotFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != package.Bundle.Bytes) throw Changed();
        var result = await Run(binding.Profile.Ssh, RemoteInstallScript.Python,
            ["stage", request.OperationId.ToString("D"), request.Sha256, package.Bundle.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), binding.Engine],
            input, progress, ct);
        if (!result.TryGetProperty("staged", out var staged) || staged.ValueKind != JsonValueKind.True ||
            result.GetProperty("bytes").GetInt64() != package.Bundle.Bytes || result.GetProperty("pid").GetInt32() <= 0 ||
            !RemoteInstallPackage.Hex(result.GetProperty("unitSha256").GetString(), 64)) throw Failure("stage_failed");
        return new(result.GetProperty("pid").GetInt32(), result.GetProperty("unitSha256").GetString()!);
    }

    internal virtual async Task CleanupAsync(RemoteInstallStart request, RemoteInstallBinding binding, CancellationToken ct)
    {
        // Cleanup follows the original captured SSH target even when its UI profile was removed during cancellation.
        if (binding.Profile.Ssh != "workbox-test" || binding.Engine != RemoteInstallTarget.Engine) throw Failure("unsupported_target");
        var result = await Run(binding.Profile.Ssh, RemoteInstallScript.Python,
            ["cleanup", request.OperationId.ToString("D"), request.Sha256, "0", binding.Engine], null, _ => { }, ct);
        if (!result.TryGetProperty("cleaned", out var cleaned) || cleaned.ValueKind != JsonValueKind.True) throw Failure("cleanup_unconfirmed");
    }

    internal virtual Task<JsonElement> ActivateAsync(RemoteInstallStart request, RemoteInstallBinding binding,
        RemoteInstallPackage.Checked package, RemoteInstallStaged staged, CancellationToken ct) =>
        Run(binding.Profile.Ssh, RemoteInstallActivation.Python,
            [request.OperationId.ToString("D"), request.Sha256, binding.Engine, package.Bundle.Commit,
                staged.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), staged.UnitSha256], null, _ => { }, ct);

    private async Task<JsonElement> Run(string host, string script, string[] arguments, Stream? input, Action<long> progress, CancellationToken ct)
    {
        var remote = "python3 -c " + Quote(script) + " " + string.Join(" ", arguments.Select(Quote));
        var command = ProcessGroup.Leading(new AgentCommand("ssh",
            ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10", "-o", "ControlMaster=no", "-o", "ControlPath=none", "--", host, remote],
            Directory.GetCurrentDirectory()));
        var start = new ProcessStartInfo(command.FileName)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in command.Arguments) start.ArgumentList.Add(arg);
        Process child;
        try { child = Process.Start(start) ?? throw Failure("ssh_unavailable"); }
        catch (Win32Exception) { throw Failure("ssh_unavailable"); }
        using var process = child;
        using var overflow = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, overflow.Token);
        var output = Drain(process.StandardOutput.BaseStream, 16384, overflow);
        var errors = Drain(process.StandardError.BaseStream, 16384, overflow);
        try
        {
            if (input is not null)
            {
                var buffer = new byte[65536];
                long transferred = 0, last = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count;
                while ((count = await input.ReadAsync(buffer, linked.Token)) > 0)
                {
                    transferred += count;
                    if (transferred > RemoteInstallPackage.MaxArchive) throw Changed();
                    hash.AppendData(buffer, 0, count);
                    await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                    if (transferred - last >= 1024 * 1024) { progress(transferred); last = transferred; }
                }
                // The remote independently compares these same uploaded bytes before retaining its stage.
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != arguments[2]) throw Changed();
                progress(transferred);
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(output, errors).WaitAsync(linked.Token);
            if (overflow.IsCancellationRequested) throw Failure("stage_failed");
            if (process.ExitCode != 0) throw Failure(process.ExitCode == 255 ? "ssh_unavailable" : "stage_failed");
            try { using var document = JsonDocument.Parse(await output); return document.RootElement.Clone(); }
            catch (JsonException) { throw Failure("stage_failed"); }
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                ProcessGroup.Kill(process.Id);
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            await process.WaitForExitAsync(cleanup.Token);
            await Task.WhenAll(output, errors).WaitAsync(cleanup.Token);
        }
    }

    private static async Task<byte[]> Drain(Stream stream, int limit, CancellationTokenSource overflow)
    {
        using var retained = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            if (retained.Length + count <= limit) retained.Write(buffer, 0, count);
            else if (!overflow.IsCancellationRequested) await overflow.CancelAsync();
        }
        return retained.ToArray();
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static CallFailed Failure(string code) => new("The remote install step could not be verified.", code);
    private static CallFailed Changed() => new("The selected bundle changed.", "bundle_changed");
}
