using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Draft.Files;

public sealed class FileOwnerTests
{
    [Fact]
    public void Two_sessions_use_their_actual_owning_roots_not_a_path_supplied_by_the_caller()
    {
        using var fixture = new EngineFixture();
        using var first = new GitFolder("a");
        using var second = new GitFolder("a");
        var sessions = SessionStore.Open(fixture.Db, fixture.Time, Path.Combine(first.Root, "checkouts"));
        var a = sessions.Create("codex", first.Root, workingFolder: first.Root);
        var b = sessions.Create("codex", second.Root, workingFolder: second.Root);
        // SessionStore's actual direct-folder session producer supplies the two roots.
        var owner = new FileOwner(sessions, fixture.Db);
        Assert.Equal(first.Root, owner.Resolve(a.SessionId, CancellationToken.None).CheckoutRoot);
        Assert.Equal(second.Root, owner.Resolve(b.SessionId, CancellationToken.None).CheckoutRoot);
        Assert.Equal(fixture.Machine, owner.Resolve(a.SessionId, CancellationToken.None).Machine);
        Assert.Throws<CallFailed>(() => owner.Resolve("missing", CancellationToken.None));
    }

    [Fact]
    public void A_remote_name_without_its_live_owner_mapping_never_reads_a_local_checkout()
    {
        using var fixture = new EngineFixture();
        using var root = new GitFolder("a");
        var sessions = SessionStore.Open(fixture.Db, fixture.Time, root.Root);
        var remote = sessions.Add(Guid.NewGuid().ToString(), "codex", root.Root, "Workbox Test");
        var failure = Assert.Throws<CallFailed>(() => new FileOwner(sessions, fixture.Db).Resolve(remote.SessionId, CancellationToken.None));
        Assert.Contains("disconnected", failure.Message);
    }
}
