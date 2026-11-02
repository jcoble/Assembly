using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-project-writer-" + Guid.NewGuid().ToString("N"));
    public GitWriterTests() => Directory.CreateDirectory(_root);

    // Pins: New Project git/clone uses the same normal writer and selected-machine relative source.
    [Fact]
    public async Task Init_and_literal_local_clone_preserve_unicode_spaces_and_a_leading_dash()
    {
        var git = new GitCli(); var writer = new GitWriter(git);
        var sourceName = "-source ü with space";
        var source = Path.Combine(_root, sourceName); Directory.CreateDirectory(source);
        Assert.Equal("Initialized repository", (await writer.InitAsync(source, default)).Message);
        await File.WriteAllTextAsync(Path.Combine(source, "body.txt"), "full body ü\0", Encoding.UTF8);
        Assert.Equal(0, (await git.RunAsync(source, ["add", "--", "body.txt"], null, null, default)).ExitCode);
        Assert.Equal(0, (await git.RunAsync(source, ["-c", "user.name=Git Test", "-c", "user.email=git@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "fixture"], null, null, default)).ExitCode);
        var destination = Path.Combine(_root, "new project ü"); Directory.CreateDirectory(destination);
        var clone = await writer.CloneAsync(sourceName, destination, default);
        Assert.Equal("Copied repository", clone.Message);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, "body.txt")), await File.ReadAllBytesAsync(Path.Combine(destination, "body.txt")));
        Assert.Equal("This folder already has a git repository.", (await Assert.ThrowsAsync<CallFailed>(() => writer.InitAsync(destination, default))).Message);
    }

    // Pins: New Project preserves failures and excludes credential-bearing source from error details.
    [Fact]
    public async Task Clone_keeps_the_exact_argument_vector_and_redacts_private_source_details()
    {
        var git = new FailedGit(); var writer = new GitWriter(git);
        const string source = "https://fake-user:fake-secret@example.invalid/owner/repo";
        var destination = Path.Combine(_root, "new folder");
        var error = await Assert.ThrowsAsync<CallFailed>(() => writer.CloneAsync(source, destination, default));
        Assert.Equal(_root, git.Folder);
        Assert.Equal(new[] { "clone", "--", source, destination }, git.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(120), git.Limit);
        Assert.StartsWith("Git couldn't sign in to the remote from this machine.", error.Message);
        Assert.DoesNotContain("fake-user", error.Message); Assert.DoesNotContain("fake-secret", error.Message);
    }
    private sealed class FailedGit : IGit
    {
        internal string? Folder;
        internal IReadOnlyList<string>? Arguments;
        internal TimeSpan? Limit;
        public Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? stdin, TimeSpan? limit, CancellationToken cancellationToken)
        {
            Folder = folder; Arguments = arguments; Limit = limit;
            return Task.FromResult(new GitRun(128, "", "Authentication failed for '" + arguments[2] + "': fake-user fake-secret"));
        }
        public IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A refused clone must not inspect or report success.");
    }
    public void Dispose() => Directory.Delete(_root, true);
}
