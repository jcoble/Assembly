using System.Runtime.CompilerServices;
using System.Text;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitTextPatchTests
{
    // Pins: g9#20
    [Fact]
    public async Task Cancelling_the_patch_read_removes_its_owned_temporary_versions()
    {
        using var cancellation = new CancellationTokenSource(); var git = new CancelRead(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitTextPatch(git).ReadAsync("file", null, Encoding.UTF8.GetBytes("old"), Encoding.UTF8.GetBytes("new"), cancellation.Token));
        Assert.NotNull(git.Folder); Assert.False(Directory.Exists(git.Folder));
    }
    private sealed class CancelRead(CancellationTokenSource cancellation) : IGit
    {
        internal string? Folder;
        public Task<GitRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan? limit, CancellationToken ct) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> args, IReadOnlyList<string>? input, [EnumeratorCancellation] CancellationToken ct)
        {
            Folder = folder; Assert.True(File.Exists(Path.Combine(folder, "a", "file"))); Assert.True(File.Exists(Path.Combine(folder, "b", "file")));
            cancellation.Cancel(); await Task.CompletedTask; ct.ThrowIfCancellationRequested(); yield return "unreachable";
        }
    }
}
