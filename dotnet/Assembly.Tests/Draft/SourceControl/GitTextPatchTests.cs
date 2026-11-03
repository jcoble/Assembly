using System.Runtime.CompilerServices;
using System.Text;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitTextPatchTests
{
    // Pins: g9#17, g9#20
    [Fact]
    public async Task Text_versions_and_a_rename_keep_the_requested_identity_and_exact_lines()
    {
        var result = await new GitTextPatch(new GitCli()).ReadAsync("new ü name.rs", "old name.rs", Encoding.UTF8.GetBytes("old\n"), Encoding.UTF8.GetBytes("new λ\n"), default);
        Assert.Equal("new ü name.rs", result.Path); Assert.Contains("-old", result.Unstaged); Assert.Contains("+new λ", result.Unstaged);
        Assert.Contains("a/old name.rs", result.Unstaged); Assert.Contains("b/new ü name.rs", result.Unstaged); Assert.False(result.Binary);
    }
    // Pins: g9#20
    [Fact]
    public async Task Missing_and_empty_versions_remain_distinct_and_non_utf8_is_binary()
    {
        var reader = new GitTextPatch(new GitCli());
        var added = await reader.ReadAsync("empty", null, null, ReadOnlyMemory<byte>.Empty, default);
        Assert.Contains("new file mode", added.Unstaged); Assert.False(added.Binary);
        var unchanged = await reader.ReadAsync("empty", null, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, default);
        Assert.Empty(unchanged.Unstaged);
        var binary = await reader.ReadAsync("binary", null, null, new byte[] { 255 }, default);
        Assert.True(binary.Binary); Assert.Empty(binary.Unstaged);
    }
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
