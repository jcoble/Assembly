using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class ProjectDirectoryClaimTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-project-claim-" + Guid.NewGuid().ToString("N"));
    public ProjectDirectoryClaimTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("empty")]
    [InlineData("nonempty")]
    [InlineData("file")]
    [InlineData("symlink")]
    public void Exclusive_reservation_preserves_every_existing_entry(string kind)
    {
        var path = Path.Combine(_root, "destination");
        if (kind == "file") File.WriteAllText(path, "keep");
        else if (kind == "symlink") Directory.CreateSymbolicLink(path, _root);
        else
        {
            Directory.CreateDirectory(path);
            if (kind == "nonempty") File.WriteAllText(Path.Combine(path, "keep.txt"), "keep");
        }
        Assert.Contains("already exists", Assert.Throws<CallFailed>(() => ProjectDirectoryClaim.Reserve(_root, "destination")).Message);
        if (kind == "file") Assert.Equal("keep", File.ReadAllText(path));
        else if (kind == "symlink") Assert.Equal(_root, new DirectoryInfo(path).LinkTarget);
        else if (kind == "nonempty") Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "keep.txt")));
        else Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacing_the_destination_or_parent_invalidates_the_claim_without_deleting_either(bool parent)
    {
        var directory = Path.Combine(_root, "parent"); Directory.CreateDirectory(directory);
        using var claim = ProjectDirectoryClaim.Reserve(directory, "new ü");
        claim.RequireCurrent();
        var moved = parent ? directory : claim.Destination;
        Directory.Move(moved, moved + "-old"); Directory.CreateDirectory(moved);
        Assert.Throws<CallFailed>(claim.RequireCurrent);
        Assert.True(Directory.Exists(moved)); Assert.True(Directory.Exists(moved + "-old"));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("close")]
    [InlineData("shutdown")]
    public async Task Completed_operation_retains_claim_until_owned_cleanup_and_never_rewrites_its_result(string cleanup)
    {
        var topics = new TopicSet();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var operations = new GitOperations(work, topics, TimeProvider.System, "owner");
        var id = Guid.NewGuid(); operations.Observe(id, "view");
        var finished = new TaskCompletionSource<GitOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = topics.Subscribe("gitOperation", operations.Key(id), _ => { }, (_, json) =>
        {
            var status = JsonSerializer.Deserialize(json, GitOperationsJson.Default.GitOperationStatus)!;
            if (status.State == "succeeded") finished.TrySetResult(status);
        });
        ProjectDirectoryClaim? claim = null;
        operations.Start(new(operations.InstanceId, id, null, "prepareProjectFolder", JsonSerializer.SerializeToElement(new { kind = "empty" })), _ =>
        {
            claim = ProjectDirectoryClaim.Reserve(_root, "new");
            operations.RetainDirectoryClaim(id, claim);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { root = claim.Destination }));
        });
        var original = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        AssertOriginal(original, operations.Status(id));
        Directory.Move(claim!.Destination, claim.Destination + "-old"); Directory.CreateDirectory(claim.Destination);
        Assert.Throws<CallFailed>(() => operations.Status(id));
        // Restore the retained inode: successful status still has the original version/result/state.
        Directory.Delete(claim.Destination); Directory.Move(claim.Destination + "-old", claim.Destination);
        AssertOriginal(original, operations.Status(id));
        if (cleanup == "release") operations.Release(id);
        else if (cleanup == "close") operations.Closed("view");
        else await operations.DisposeAsync();
        Assert.Equal("uncertain", operations.Status(id).State);
        Assert.Throws<CallFailed>(claim.RequireCurrent);
        Assert.True(Directory.Exists(claim.Destination));
    }
    private static void AssertOriginal(GitOperationStatus expected, GitOperationStatus actual)
    {
        Assert.Equal(expected.State, actual.State); Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Result!.Value.GetRawText(), actual.Result!.Value.GetRawText());
        Assert.Equal(expected.Error, actual.Error);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
