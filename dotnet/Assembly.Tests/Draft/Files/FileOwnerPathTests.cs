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
    public void A_path_target_answers_the_real_parent_and_file_name()
    {
        var file = Write("a.txt");
        var owner = Resolve(file);
        Assert.Equal(new FileOwnership(_fixture.Machine, Physical(_folder), "", "a.txt"), owner);
    }

    [Fact]
    public void A_symlink_answers_its_target_s_real_path()
    {
        Write("real/b.txt");
        File.CreateSymbolicLink(Path.Combine(_folder, "link.txt"), "real/b.txt");
        Directory.CreateSymbolicLink(Path.Combine(_folder, "linked"), Path.Combine(_folder, "real"));
        var expected = new FileOwnership(_fixture.Machine, Physical(Path.Combine(_folder, "real")), "", "b.txt");
        Assert.Equal(expected, Resolve(Path.Combine(_folder, "link.txt")));
        Assert.Equal(expected, Resolve(Path.Combine(_folder, "linked", "b.txt")));
    }

    [Fact]
    public void A_link_and_its_target_answer_the_same_owner()
    {
        var target = Write("real/b.txt");
        File.CreateSymbolicLink(Path.Combine(_folder, "link.txt"), target);
        File.CreateSymbolicLink(Path.Combine(_folder, "chain.txt"), "link.txt");
        Directory.CreateSymbolicLink(Path.Combine(_folder, "linked"), "real");
        var owner = Resolve(target);
        Assert.Equal(owner, Resolve(Path.Combine(_folder, "link.txt")));
        Assert.Equal(owner, Resolve(Path.Combine(_folder, "chain.txt")));
        Assert.Equal(owner, Resolve(Path.Combine(_folder, "linked", "b.txt")));
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
    public void Tilde_expands_to_this_backend_s_home()
    {
        var home = Path.Combine(_folder, "home");
        Write("home/a.txt");
        var before = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);
            Assert.Equal(new FileOwnership(_fixture.Machine, Physical(home), "", "a.txt"), Resolve("~/a.txt"));
            Assert.Equal("not_regular", Refused("~").Reason);
        }
        finally { Environment.SetEnvironmentVariable("HOME", before); }
    }

    [Fact]
    public void A_tilde_with_a_user_name_is_not_a_full_path() =>
        Assert.Equal("not_full_path", Refused("~bob/a.txt").Reason);

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
    public void Relative_and_dot_paths_are_not_full_paths()
    {
        Write("x/a");
        foreach (var path in new[] { "a.txt", "", Path.Combine(_folder, "x", ".", "a"), _folder + "/x/../x/a", "~/../a" })
        {
            var failure = Refused(path);
            Assert.Equal("not_full_path", failure.Reason);
            Assert.Equal("That file link isn't a full path.", failure.Message);
        }
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
    public void A_missing_or_unreadable_file_says_so_without_a_code()
    {
        var missing = Refused(Path.Combine(_folder, "missing.txt"));
        Assert.Null(missing.Reason);
        Assert.Equal("that file isn't there", missing.Message);
        Assert.Null(Refused(Path.Combine(_folder, "no-folder", "a.txt")).Reason);
        if (OperatingSystem.IsWindows()) return;
        var locked = Write("locked.txt");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        if (Opens(locked)) return; // Root reads a mode-000 file, so this half needs a user it applies to.
        var unreadable = Refused(locked);
        Assert.Null(unreadable.Reason);
        Assert.Equal("that file can't be read", unreadable.Message);
    }

    [Fact]
    public void A_file_under_a_folder_with_no_search_permission_says_it_can_t_be_read()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = Write("sealed/a.txt");
        var folder = Path.GetDirectoryName(file)!;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            if (Opens(file)) return; // Root searches any folder, so this needs a user it applies to.
            var failure = Refused(file);
            Assert.Null(failure.Reason);
            Assert.Equal("that file can't be read", failure.Message);
        }
        finally { File.SetUnixFileMode(folder, mode); }
    }

    [Fact]
    public void A_path_of_exactly_the_limit_is_not_refused_for_its_length()
    {
        var path = "/" + new string('a', 4095);
        Assert.Equal(4096, path.Length);
        Assert.NotEqual("not_a_path", Refused(path).Reason);
    }

    [Fact]
    public void Inside_a_session_s_checkout_answers_that_session_and_the_relative_path()
    {
        using var checkout = new GitFolder("src/a.txt");
        var session = _sessions.Create("codex", checkout.Root, workingFolder: checkout.Root);
        var root = _owner.Resolve(session.SessionId, CancellationToken.None).CheckoutRoot;
        var owner = Resolve(Path.Combine(checkout.Root, "src", "a.txt"), session.SessionId);
        Assert.Equal(new FileOwnership(_fixture.Machine, root, session.SessionId, "src/a.txt"), owner);
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

    [Fact]
    public void A_path_target_and_its_answer_cross_the_wire()
    {
        var target = JsonSerializer.Deserialize("""{"path":"/a/b.txt","machine":"Workbox Test","insideOf":"s"}""", FilesJson.Default.PathTarget);
        Assert.Equal(new PathTarget("/a/b.txt", "Workbox Test", "s"), target);
        var answer = JsonSerializer.Serialize(new FileOwnership("m", "/a", "", "b.txt"), FilesJson.Default.FileOwnership);
        Assert.Equal("""{"machine":"m","checkoutRoot":"/a","remoteSessionId":"","file":"b.txt"}""", answer);
        // A session answer from before this field still equals the ownership it names.
        var session = JsonSerializer.Deserialize("""{"machine":"m","checkoutRoot":"/a","remoteSessionId":"s"}""", FilesJson.Default.FileOwnership);
        Assert.Equal(new FileOwnership("m", "/a", "s"), session);
    }

    private FileOwnership Resolve(string path, string? insideOf = null) =>
        _owner.ResolvePath(new PathTarget(path, InsideOf: insideOf), CancellationToken.None);

    private CallFailed Refused(string path) => Assert.Throws<CallFailed>(() => Resolve(path));

    private static bool Opens(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

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
