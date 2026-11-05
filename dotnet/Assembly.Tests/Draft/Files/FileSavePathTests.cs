using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

/// <summary>A path target's rechecks, owner-only rule and plain refusals, on the real feature's handlers.</summary>
public sealed class FileSavePathTests : IDisposable
{
    private const string Moved = "the file moved; the draft is preserved";
    private readonly DirectoryInfo _base = Directory.CreateTempSubdirectory("file-save-path-");

    private string Folder => Path.Combine(_base.FullName, "folder");
    private string Target => Path.Combine(Folder, "a.txt");
    private string Parked => Path.Combine(_base.FullName, "parked");
    private string Elsewhere => Path.Combine(_base.FullName, "elsewhere");

    public FileSavePathTests()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(Elsewhere);
        File.WriteAllText(Target, "old");
        File.WriteAllText(Path.Combine(Elsewhere, "a.txt"), "other");
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _base.Delete(recursive: true);
    }

    /// <summary>The file's folder moves away and a link elsewhere takes its name, so the path now means another file.</summary>
    private void SwapFolderForALink()
    {
        Directory.Move(Folder, Parked);
        Directory.CreateSymbolicLink(Folder, Elsewhere);
    }

    [Fact]
    public async Task Commit_refuses_when_the_real_path_moved_and_keeps_the_stage()
    {
        await using var rig = new FilesRig();
        var upload = await rig.StagedAsync(Target, "new");
        SwapFolderForALink();
        var error = await Assert.ThrowsAsync<CallFailed>(() => rig.CallAsync("commitSave", $$$"""{"upload":"{{{upload}}}"}"""));
        Assert.Equal(Moved, error.Message);
        Assert.Equal("checkout_changed", error.Reason);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(Parked, "a.txt")));
        Assert.Equal("other", await File.ReadAllTextAsync(Path.Combine(Elsewhere, "a.txt")));
        // The stage went with the folder; the upload's own path to it now names nothing, so it stays.
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(Parked, ".assembly-save-" + upload)));
    }

    [Fact]
    public async Task Compare_refuses_when_the_real_path_moved()
    {
        await using var rig = new FilesRig();
        var upload = await rig.StagedAsync(Target, "new");
        SwapFolderForALink();
        var error = await Assert.ThrowsAsync<CallFailed>(() => rig.CallAsync("compare", $$$"""{"upload":"{{{upload}}}"}"""));
        Assert.Equal(Moved, error.Message);
    }

    [Fact]
    public async Task A_file_gone_before_commit_or_compare_is_the_usual_conflict_by_path_target()
    {
        await using var rig = new FilesRig();
        var committing = await rig.StagedAsync(Target, "new");
        var comparing = await rig.StagedAsync(Target, "new");
        File.Delete(Target);
        var compared = await rig.CallAsync("compare", $$$"""{"upload":"{{{comparing}}}"}""");
        Assert.Equal(JsonValueKind.Null, compared.GetProperty("diskRevision").ValueKind);
        var saved = await rig.CallAsync("commitSave", $$$"""{"upload":"{{{committing}}}"}""");
        Assert.Equal("conflict", saved.GetProperty("state").GetString());
        Assert.Equal("revision_changed", saved.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task A_read_whose_real_path_moved_mid_read_is_refused()
    {
        await using var rig = new FilesRig();
        var target = new PathTarget(Target);
        var owner = rig.Owners.ResolvePath(target, default);
        var error = await Assert.ThrowsAsync<CallFailed>(() => new FileReadSession().ReadAsync(owner.CheckoutRoot, owner.File!, 0, null, "c", default,
            recheck: () =>
            {
                SwapFolderForALink();
                rig.Owners.ResolvePath(target, default, owner);
            }));
        Assert.Equal(Moved, error.Message);
    }

    [Fact]
    public async Task A_read_held_by_a_path_target_is_released_after_the_file_became_a_link()
    {
        await File.WriteAllTextAsync(Target, new string('x', FileRead.ChunkBytes + 1));
        await using var rig = new FilesRig();
        var read = await rig.CallAsync("read", $$$"""{"target":{"path":"{{{Target}}}"}}""");
        var owner = await rig.CallAsync("owner", $$$"""{"target":{"path":"{{{Target}}}"}}""");
        File.Delete(Target);
        File.CreateSymbolicLink(Target, Path.Combine(Elsewhere, "a.txt"));
        var released = await rig.CallAsync("release",
            $$$"""{"target":{"path":"{{{Target}}}"},"revision":"{{{read.GetProperty("revision").GetString()}}}","expectedOwner":{{{owner.GetRawText()}}}}""");
        Assert.True(released.GetProperty("released").GetBoolean());
    }

    [Fact]
    public async Task An_unwritable_folder_says_so_in_plain_words()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var rig = new FilesRig();
        var revision = await FileSave.RevisionAsync(Target, default);
        File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (CanCreateIn(Folder)) return; // Root writes anywhere: this reports passed without having run.
        var error = await Assert.ThrowsAsync<CallFailed>(() => rig.CallAsync("beginSave",
            $$$"""{"target":{"path":"{{{Target}}}"},"expectedRevision":"{{{revision}}}","size":3}"""));
        Assert.Equal("That folder can't be written, so the file wasn't saved.", error.Message);
        Assert.Equal("not_writable", error.Reason);
        Assert.Equal("old", await File.ReadAllTextAsync(Target));
    }

    [Fact]
    public async Task A_non_owner_peer_is_forbidden_on_a_path_target()
    {
        await using var rig = new FilesRig();
        var upload = await rig.StagedAsync(Target, "new");
        var phone = new PhonePeer("phone");
        var target = $$$"""{"path":"{{{Target}}}"}""";
        var revision = new string('0', 64);
        foreach (var (method, args) in new[]
        {
            ("owner", $$$"""{"target":{{{target}}}}"""),
            ("read", $$$"""{"target":{{{target}}}}"""),
            ("release", $$$"""{"target":{{{target}}},"revision":"{{{revision}}}","expectedOwner":{"machine":"m","checkoutRoot":"{{{Folder}}}","remoteSessionId":"","file":"a.txt"}}"""),
            ("beginSave", $$$"""{"target":{{{target}}},"expectedRevision":"{{{revision}}}","size":0}"""),
            ("commitSave", $$$"""{"upload":"{{{upload}}}"}"""),
        })
        {
            var error = await Assert.ThrowsAsync<CallFailed>(() => rig.CallAsync(method, args, phone));
            Assert.Equal("forbidden", error.Reason);
        }
        Assert.Equal("old", await File.ReadAllTextAsync(Target));
    }

    [Fact]
    public async Task A_non_owner_peer_learns_nothing_about_a_path_from_the_order_key()
    {
        await using var rig = new FilesRig();
        var revision = new string('0', 64);
        Assert.Null(rig.Order("beginSave", $$$"""{"target":{"path":"{{{Target}}}.missing"},"expectedRevision":"{{{revision}}}","size":0}"""));
        Assert.Null(rig.Order("beginSave", $$$"""{"target":{"path":"/proc/self/status"},"expectedRevision":"{{{revision}}}","size":0}"""));
        var error = await Assert.ThrowsAsync<CallFailed>(() => rig.CallAsync("beginSave",
            $$$"""{"target":{"path":"{{{Target}}}.missing"},"expectedRevision":"{{{revision}}}","size":0}""", new PhonePeer("phone")));
        Assert.Equal("forbidden", error.Reason);
    }

    [Fact]
    public async Task A_begin_by_path_target_orders_on_the_file_s_real_path()
    {
        await using var rig = new FilesRig();
        var link = Path.Combine(_base.FullName, "link.txt");
        File.CreateSymbolicLink(link, Target);
        var revision = new string('0', 64);
        var real = FileOwner.RealFile(Target);
        Assert.Equal("files:" + real, rig.Order("beginSave", $$$"""{"target":{"path":"{{{Target}}}"},"expectedRevision":"{{{revision}}}","size":0}"""));
        Assert.Equal("files:" + real, rig.Order("beginSave", $$$"""{"target":{"path":"{{{link}}}"},"expectedRevision":"{{{revision}}}","size":0}"""));
    }

    [Fact]
    public void A_file_directly_under_the_top_folder_resolves()
    {
        Assert.Equal("/a.txt", SessionPaths.ResolveFile("/", "a.txt"));
        Assert.Throws<CallFailed>(() => SessionPaths.ResolveFile("/", "../a.txt"));
    }

    private static bool CanCreateIn(string folder)
    {
        var probe = Path.Combine(folder, ".probe");
        try { File.WriteAllText(probe, ""); File.Delete(probe); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
