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
    public void A_session_with_no_folder_says_no_folder()
    {
        using var fixture = new EngineFixture();
        using var root = new GitFolder("a");
        var sessions = SessionStore.Open(fixture.Db, fixture.Time, root.Root);
        // An id that can't name a managed folder, with no working folder: nothing to resolve.
        var bare = sessions.Add("not/a/name", "codex", "repo", "");
        var failure = Assert.Throws<CallFailed>(() => new FileOwner(sessions, fixture.Db).Resolve(bare.SessionId, CancellationToken.None));
        Assert.Equal("no_folder", failure.Reason);
        Assert.Equal("this session has no folder", failure.Message);
    }

    [Fact]
    public void A_missing_folder_before_the_first_message_is_not_made()
    {
        using var fixture = new EngineFixture();
        using var root = new GitFolder("a");
        var sessions = SessionStore.Open(fixture.Db, fixture.Time, root.Root);
        var waiting = sessions.Create("codex", "repo");
        var failure = Assert.Throws<CallFailed>(() => new FileOwner(sessions, fixture.Db).Resolve(waiting.SessionId, CancellationToken.None));
        Assert.Equal("folder_not_made", failure.Reason);
        Assert.Equal("this session's folder is made on its first message", failure.Message);
    }

    [Fact]
    public void A_missing_folder_after_a_turn_is_a_deleted_checkout()
    {
        using var fixture = new EngineFixture();
        using var root = new GitFolder("a");
        var sessions = SessionStore.Open(fixture.Db, fixture.Time, root.Root);
        var ran = sessions.Create("codex", "repo");
        sessions.SetAcpSession(ran.SessionId, "provider-session");
        var failure = Assert.Throws<CallFailed>(() => new FileOwner(sessions, fixture.Db).Resolve(ran.SessionId, CancellationToken.None));
        Assert.Equal("checkout_unavailable", failure.Reason);
        Assert.Equal("Checkout/Worktree deleted.", failure.Message);
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
