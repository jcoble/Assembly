using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Draft.Files;

[CollectionDefinition("home", DisableParallelization = true)]
public sealed class HomeCollection;

/// <summary>The path rule: a caller's absolute path names any regular file this backend's user can read.</summary>
[Collection("home")]
public sealed class FileOwnerPathTests : IDisposable
{
    private readonly EngineFixture _fixture = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("files-path-").FullName;
    private readonly SessionStore _sessions;
    private readonly FileOwner _owner;

    public FileOwnerPathTests()
    {
        _sessions = SessionStore.Open(_fixture.Db, _fixture.Time, Path.Combine(_folder, "checkouts"));
        _owner = new FileOwner(_sessions, _fixture.Db);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_link_loop_ends_in_a_refusal_without_a_code()
    {
        File.CreateSymbolicLink(Path.Combine(_folder, "one"), "two");
        File.CreateSymbolicLink(Path.Combine(_folder, "two"), "one");
        var failure = Refused(Path.Combine(_folder, "one"));
        Assert.Null(failure.Reason);
        Assert.Equal("that path has too many links", failure.Message);
    }

    [Fact]
    public void Nul_and_overlong_paths_are_not_file_paths()
    {
        foreach (var path in new[] { Path.Combine(_folder, "a\0b"), "/" + new string('a', 4096) })
        {
            var failure = Refused(path);
            Assert.Equal("not_a_path", failure.Reason);
            Assert.Equal("That file link isn't a file path.", failure.Message);
        }
        var absent = Assert.Throws<CallFailed>(() => _owner.ResolvePath(new PathTarget(null!), CancellationToken.None));
        Assert.Equal("not_a_path", absent.Reason);
    }

    [Fact]
    public void Proc_sys_and_dev_are_not_regular_files()
    {
        // /dev/null is seekable, so the /dev rule (not the type check) is what refuses it.
        var paths = new List<string> { "/proc/self/status", "/dev/null" };
        var sys = Directory.Exists("/sys/kernel") ? Directory.EnumerateFiles("/sys/kernel").FirstOrDefault() : null;
        if (sys is not null) paths.Add(sys);
        File.CreateSymbolicLink(Path.Combine(_folder, "status"), "/proc/self/status");
        paths.Add(Path.Combine(_folder, "status"));
        foreach (var path in paths)
        {
            var failure = Refused(path);
            Assert.Equal("not_regular", failure.Reason);
            Assert.Equal("That isn't a regular file, so it can't open.", failure.Message);
        }
    }

    [Fact]
    public async Task A_fifo_a_socket_and_a_folder_are_not_regular_files()
    {
        var fifo = Path.Combine(_folder, "fifo");
        using (var mkfifo = Process.Start("mkfifo", [fifo])) await mkfifo.WaitForExitAsync();
        Assert.True(File.Exists(fifo));
        var socketPath = Path.Combine(_folder, "socket");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.CreateSymbolicLink(Path.Combine(_folder, "fifo-link"), "fifo");
        Directory.CreateDirectory(Path.Combine(_folder, "folder"));
        foreach (var path in new[] { fifo, socketPath, Path.Combine(_folder, "fifo-link"), Path.Combine(_folder, "folder") })
        {
            // A blocking open would hang here; the timeout turns that into a failure.
            var resolving = Task.Run(() => Resolve(path));
            var failure = await Assert.ThrowsAsync<CallFailed>(() => resolving.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("not_regular", failure.Reason);
        }
    }

    [Fact]
    public void A_checkout_reached_through_a_link_still_counts_as_inside()
    {
        using var checkout = new GitFolder("src/a.txt");
        var link = Path.Combine(_folder, "checkout-link");
        Directory.CreateSymbolicLink(link, checkout.Root);
        var session = _sessions.Create("codex", link, workingFolder: link);
        var root = _owner.Resolve(session.SessionId, CancellationToken.None).CheckoutRoot;
        var expected = new FileOwnership(_fixture.Machine, root, session.SessionId, "src/a.txt");
        Assert.Equal(expected, Resolve(Path.Combine(checkout.Root, "src", "a.txt"), session.SessionId));
        Assert.Equal(expected, Resolve(Path.Combine(link, "src", "a.txt"), session.SessionId));
    }

    [Fact]
    public void Outside_the_named_session_answers_an_outside_file()
    {
        using var checkout = new GitFolder("a.txt");
        var session = _sessions.Create("codex", checkout.Root, workingFolder: checkout.Root);
        var sibling = checkout.Root + "-sibling";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "a.txt"), "x");
            Assert.Equal(new FileOwnership(_fixture.Machine, Physical(sibling), "", "a.txt"),
                Resolve(Path.Combine(sibling, "a.txt"), session.SessionId));
            var outside = Write("b.txt");
            Assert.Equal(new FileOwnership(_fixture.Machine, Physical(_folder), "", "b.txt"), Resolve(outside, session.SessionId));
            // A session that can't be resolved is only a missing hint: the file is outside, not refused.
            Assert.Equal(new FileOwnership(_fixture.Machine, Physical(_folder), "", "b.txt"), Resolve(outside, "missing"));
        }
        finally { Directory.Delete(sibling, recursive: true); }
    }

    [Fact]
    public void An_expected_owner_that_differs_is_refused_as_changed()
    {
        var file = Write("a.txt");
        var actual = Resolve(file);
        Assert.Equal(actual, _owner.ResolvePath(new PathTarget(file), CancellationToken.None, actual));
        var failure = Assert.Throws<CallFailed>(() =>
            _owner.ResolvePath(new PathTarget(file), CancellationToken.None, actual with { File = "other.txt" }));
        Assert.Equal("checkout_changed", failure.Reason);
        Assert.Equal("the file moved; the draft is preserved", failure.Message);
    }

    [Fact]
    public void A_target_naming_a_machine_is_never_resolved_here()
    {
        var file = Write("a.txt");
        var failure = Assert.Throws<CallFailed>(() =>
            _owner.ResolvePath(new PathTarget(file, Machine: "Workbox Test"), CancellationToken.None));
        Assert.Equal("this machine's files must be opened on it", failure.Message);
    }

    private FileOwnership Resolve(string path, string? insideOf = null) =>
        _owner.ResolvePath(new PathTarget(path, InsideOf: insideOf), CancellationToken.None);

    private CallFailed Refused(string path) => Assert.Throws<CallFailed>(() => Resolve(path));

    private string Write(string relative)
    {
        var full = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return full;
    }

    /// <summary>The folder's physical path, as the shell reports it (on a Mac, /var is a link to /private/var).</summary>
    private static string Physical(string folder)
    {
        using var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "cd \"$1\" && pwd -P", "sh", folder])
        { RedirectStandardOutput = true })!;
        var output = shell.StandardOutput.ReadToEnd().TrimEnd('\n');
        shell.WaitForExit();
        return output;
    }
}
