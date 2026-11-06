using Assembly.Application.Common.Calls;
using Assembly.Remote.Cli;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliOwnerIdentityTests
{
    [Fact]
    public async Task Actual_owner_home_retains_one_private_identity_across_reopen()
    {
        var home = Directory.CreateTempSubdirectory("assembly-cli-owner-").FullName;
        try
        {
            var first = await RemoteCliOwnerIdentity.ReadOrCreateAsync(home, default);
            var reopened = await RemoteCliOwnerIdentity.ReadOrCreateAsync(home, default);
            Assert.True(Guid.TryParseExact(first, "N", out _));
            Assert.Equal(first, reopened);
            var file = Assert.Single(Directory.GetFiles(home));
            Assert.Equal(first, await File.ReadAllTextAsync(file));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task Malformed_retained_identity_is_refused_without_replacement()
    {
        var home = Directory.CreateTempSubdirectory("assembly-cli-owner-invalid-").FullName;
        try
        {
            var path = Path.Combine(home, "cli-installation-id");
            await File.WriteAllTextAsync(path, "keep-invalid-owner-state");
            await Assert.ThrowsAsync<CallFailed>(() => RemoteCliOwnerIdentity.ReadOrCreateAsync(home, default));
            Assert.Equal("keep-invalid-owner-state", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(home));
        }
        finally { Directory.Delete(home, true); }
    }
}
