using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileRelayScopesTests
{
    private static CallContext Owner(string connection) => new(CancellationToken.None, connection, Peer: new OwnerPeer());

    [Fact]
    public async Task Two_desktop_scopes_on_one_relay_keep_separate_read_limits_and_cleanup()
    {
        using var folder = new GitFolder();
        for (var i = 0; i < 8; i++)
            await File.WriteAllTextAsync(Path.Combine(folder.Root, i.ToString()), new string('x', FileRead.ChunkBytes + 1));
        var reads = new FileReadSession();
        using var scopes = new FileRelayScopes(reads.Close);
        var first = scopes.Open(Owner("normal"));
        var second = scopes.Open(Owner("normal"));
        scopes.Attach(first.Scope, first.Attachment, Owner("bulk"));
        scopes.Attach(second.Scope, second.Attachment, Owner("bulk"));
        using var a = scopes.Bind(first.Scope, Owner("bulk"));
        using var b = scopes.Bind(second.Scope, Owner("bulk"));
        for (var i = 0; i < 4; i++)
        {
            await reads.ReadAsync(folder.Root, i.ToString(), 0, null, a.Context.Connection, a.Context.Cancellation);
            await reads.ReadAsync(folder.Root, (i + 4).ToString(), 0, null, b.Context.Connection, b.Context.Cancellation);
        }
        var held = reads.HeldBytes;
        Assert.True(scopes.Release(first.Scope, "normal"));
        Assert.Equal(held / 2, reads.HeldBytes);
        Assert.True(a.Context.Cancellation.IsCancellationRequested);
        Assert.False(b.Context.Cancellation.IsCancellationRequested);
        scopes.Close("bulk");
        Assert.Equal(0, reads.HeldBytes);
        Assert.True(b.Context.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void Only_owner_calls_can_attach_and_only_the_original_normal_socket_can_release()
    {
        var released = new List<string>();
        using var scopes = new FileRelayScopes(released.Add);
        var scope = scopes.Open(Owner("normal"));
        Assert.Throws<CallFailed>(() => scopes.Bind(scope.Scope, new(CancellationToken.None, "phone", Peer: new PhonePeer("p"))));
        Assert.Throws<CallFailed>(() => scopes.Bind(scope.Scope, Owner("bulk")));
        Assert.Throws<CallFailed>(() => scopes.Attach(scope.Scope, "wrong", Owner("bulk")));
        scopes.Attach(scope.Scope, scope.Attachment, Owner("bulk"));
        using var bulk = scopes.Bind(scope.Scope, Owner("bulk"));
        Assert.Throws<CallFailed>(() => scopes.Bind(scope.Scope, Owner("third")));
        Assert.False(scopes.Release(scope.Scope, "bulk"));
        Assert.Empty(released);
        scopes.Close("normal");
        Assert.Equal(bulk.Context.Connection, Assert.Single(released));
        Assert.Throws<CallFailed>(() => scopes.Bind(scope.Scope, Owner("normal")));
        Assert.False(scopes.Release(scope.Scope, "normal"));
    }

    [Fact]
    public void Closing_a_socket_invalidates_inflight_calls_and_a_late_bind_cannot_reopen_its_scope()
    {
        var released = new List<string>();
        using var scopes = new FileRelayScopes(released.Add);
        var scope = scopes.Open(Owner("normal"));
        scopes.Attach(scope.Scope, scope.Attachment, Owner("bulk"));
        using var call = scopes.Bind(scope.Scope, Owner("bulk"));
        scopes.Close("bulk");
        Assert.True(call.Context.Cancellation.IsCancellationRequested);
        Assert.Throws<CallFailed>(() => scopes.Bind(scope.Scope, Owner("replacement-bulk")));
        Assert.Single(released);
        scopes.Close("normal");
        Assert.Single(released);
    }

    [Fact]
    public void Failed_open_reply_releases_only_that_replys_grant_and_attachment_cannot_be_reused()
    {
        var released = new List<string>();
        using var scopes = new FileRelayScopes(released.Add);
        var firstCall = Owner("normal") with { ReplyId = 1 };
        var first = scopes.Open(firstCall);
        var second = scopes.Open(Owner("normal") with { ReplyId = 2 });
        scopes.FailedOpen(firstCall);
        Assert.Throws<CallFailed>(() => scopes.Attach(first.Scope, first.Attachment, Owner("bulk")));
        scopes.Attach(second.Scope, second.Attachment, Owner("bulk"));
        Assert.Throws<CallFailed>(() => scopes.Attach(second.Scope, second.Attachment, Owner("third")));
        using var call = scopes.Bind(second.Scope, Owner("bulk"));
        Assert.False(call.Context.Cancellation.IsCancellationRequested);
        Assert.Single(released);
    }

    [Fact]
    public void A_bound_write_sees_socket_close_before_the_closed_callback_drains_queued_writes()
    {
        using var socketClose = new CancellationTokenSource();
        var released = new List<string>();
        using var scopes = new FileRelayScopes(released.Add);
        var scope = scopes.Open(Owner("normal"));
        var bulk = Owner("bulk") with { Closed = socketClose.Token };
        scopes.Attach(scope.Scope, scope.Attachment, bulk);
        using var call = scopes.Bind(scope.Scope, bulk);
        socketClose.Cancel();
        Assert.True(call.Context.Closed.IsCancellationRequested);
        Assert.True(call.Context.Cancellation.IsCancellationRequested);
        Assert.Empty(released);
        scopes.Close("bulk");
        Assert.Single(released);
    }
}
