using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat;

namespace Assembly.Tests.Draft.Files;

public sealed class FilePathTests
{
    [Theory]
    [InlineData("a/../b")]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("a\u0000b")]
    public void File_paths_refuse_unsafe_components_before_io(string path)
    {
        using var folder = new GitFolder();
        Assert.Throws<CallFailed>(() => SessionPaths.ResolveFile(folder.Root, path));
    }

    [Fact]
    public void Root_inspection_is_allowed_but_root_mutation_is_not()
    {
        using var folder = new GitFolder();
        Assert.Equal(folder.Root, SessionPaths.ResolveFile(folder.Root, "", allowRoot: true));
        Assert.Throws<CallFailed>(() => SessionPaths.ResolveFile(folder.Root, ""));
        Assert.Throws<CallFailed>(() => SessionPaths.ResolveFile(folder.Root, new string('x', 4097)));
    }
}
