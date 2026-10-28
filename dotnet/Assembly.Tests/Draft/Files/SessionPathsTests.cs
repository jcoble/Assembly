using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat;

namespace Assembly.Tests.Draft.Files;

public sealed class SessionPathsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("session-paths-");

    public void Dispose() => _root.Delete(recursive: true);

    // Pins: files#10
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("./")]
    public void The_root_itself_resolves_to_the_root(string relative)
    {
        Assert.Equal(_root.FullName, SessionPaths.Resolve(_root.FullName, relative));
    }

    // Pins: files#10
    [Fact]
    public void A_path_below_the_root_resolves_under_it()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "src", "app"));
        Assert.Equal(Path.Combine(_root.FullName, "src", "app"), SessionPaths.Resolve(_root.FullName, "src/app"));
        Assert.Equal(Path.Combine(_root.FullName, "src"), SessionPaths.Resolve(_root.FullName, "src/app/.."));
    }

    // Pins: files#10
    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("src/../../other")]
    [InlineData("/etc")]
    public void A_path_that_leaves_the_root_is_refused(string relative)
    {
        Assert.Throws<CallFailed>(() => SessionPaths.Resolve(_root.FullName, relative));
    }

    // Pins: files#10
    [Fact]
    public void A_sibling_folder_that_shares_the_root_s_name_as_a_prefix_is_refused()
    {
        var name = Path.GetFileName(_root.FullName);
        Assert.Throws<CallFailed>(() => SessionPaths.Resolve(_root.FullName, $"../{name}-other"));
    }

    // Pins: files#10
    [Fact]
    public void A_path_through_a_link_is_refused_even_when_it_points_inside()
    {
        var outside = Directory.CreateTempSubdirectory("session-paths-outside-");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root.FullName, "out"), outside.FullName);
            Directory.CreateDirectory(Path.Combine(_root.FullName, "in"));
            Directory.CreateSymbolicLink(Path.Combine(_root.FullName, "loop"), Path.Combine(_root.FullName, "in"));
            Assert.Throws<CallFailed>(() => SessionPaths.Resolve(_root.FullName, "out"));
            Assert.Throws<CallFailed>(() => SessionPaths.Resolve(_root.FullName, "out/x"));
            Assert.Throws<CallFailed>(() => SessionPaths.Resolve(_root.FullName, "loop"));
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }
}
