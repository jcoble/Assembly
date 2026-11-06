using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Updates;
using Assembly.Remote.Cli;
using Assembly.Remote.Serve;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliCaptureFactoryTests
{
    [Fact]
    public async Task Actual_runtime_and_custom_source_are_published_at_only_the_owner_chosen_location()
    {
        using var f = new CliCheckFixture(); var source = RemoteCliCaptureTests.Capture(f).AgentsFile;
        var file = Path.Combine(f.Home, "owner-selected-private-capture");
        var options = new ServeOptions(9876, f.Home, Path.Combine(f.Home, "unused-token-file"), source);
        var installationId = Guid.NewGuid().ToString("N");
        var result = await RemoteCliCaptureFactory.PublishAsync(options, f.Launcher, "actual-machine", installationId, file, default);
        var saved = RemoteCliCapture.Load(file);
        Assert.Equal(installationId, saved.InstallationId); Assert.Equal(source, saved.AgentsFile);
        Assert.Equal(options.Home, saved.Home); Assert.Equal("actual-machine", saved.MachineId);
        Assert.Equal(Environment.ProcessPath, saved.HelperCommand.FileName);
        var entry = Environment.GetCommandLineArgs()[0];
        Assert.Equal(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { entry } : [], saved.HelperCommand.Arguments);
        Assert.Equal(result.Targets.Single().ConfigurationTargetId, saved.Targets.Single().ConfigurationTargetId);
        Assert.False(File.Exists(options.TokenFile));
        Assert.False(File.Exists(Path.Combine(f.Home, "probe-record")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_runtime_vector_executes_the_actual_fixed_read_helper(bool apphost)
    {
        using var f = new CliCheckFixture(); var capture = RemoteCliCaptureTests.Capture(f);
        var file = Path.Combine(f.Home, "private-capture");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(p => Path.Combine(p, "dotnet")).First(File.Exists);
        var assembly = typeof(ServeOptions).Assembly.Location;
        var command = RemoteCliCaptureFactory.Invocation(apphost ? Apphost() : dotnet, assembly, f.Home);
        capture = capture with { HelperCommand = command };
        await RemoteCliCapture.SaveAsync(file, capture, default);
        var request = JsonSerializer.SerializeToUtf8Bytes(f.Args, UpdatesJson.Default.CliCheckArgs);
        var output = await new SshUpdateProcess(TimeProvider.System).ReadAsync(new(command with
        { Arguments = [.. command.Arguments, "cli-read", "--capture", file] }, Request: request), default, default);
        var result = JsonSerializer.Deserialize(output, RemoteCliJson.Default.RemoteCliReadResult)!;
        Assert.Equal(f.Version, result.InstalledVersion); Assert.Equal("npm", result.Source);
        Assert.Equal(capture.InstallationId, result.InstallationId);
    }

    [Fact]
    public void Native_entry_keeps_no_managed_assembly_or_serve_arguments()
    {
        // This checks native-vector selection only; an installed native artifact is separate acceptance proof.
        var executable = Apphost();
        var command = RemoteCliCaptureFactory.Invocation(executable, executable, Environment.CurrentDirectory);
        Assert.Equal(executable, command.FileName); Assert.Empty(command.Arguments); Assert.Null(command.Environment);
    }
    private static string Apphost() => Path.Combine(Path.GetDirectoryName(typeof(ServeOptions).Assembly.Location)!,
        "Assembly.Remote" + (OperatingSystem.IsWindows() ? ".exe" : ""));
}
