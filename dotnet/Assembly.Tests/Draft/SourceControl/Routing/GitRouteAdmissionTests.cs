using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitRouteAdmissionTests
{
    [Fact]
    public void The_read_gate_uses_the_actual_registered_kind_and_rejects_writes_and_wrong_bulk_channel()
    {
        using var file = new TempDatabase();
        var topics = new Assembly.Infrastructure.Topics.Topics();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var calls = new SocketDispatcher(db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        calls.Read("git", "status", GitOperationsJson.Default.JsonElement, GitOperationsJson.Default.JsonElement,
            (value, _) => Task.FromResult(value));
        calls.Read("git", "pr", GitOperationsJson.Default.JsonElement, GitOperationsJson.Default.JsonElement,
            (value, _) => Task.FromResult(value), bulk: true);
        calls.Write("git", "commit", GitOperationsJson.Default.JsonElement, GitOperationsJson.Default.JsonElement,
            (value, _) => Task.FromResult<Func<Assembly.Application.Common.Database.IWriteScope, System.Text.Json.JsonElement>>(_ => value));
        Assert.Equal(CallKind.Write, calls.KindOf("git", "commit"));
        Assert.True(MachineReads.AllowRead(calls, "git", "status", false));
        Assert.True(MachineReads.AllowRead(calls, "git", "pr", true));
        Assert.False(MachineReads.AllowRead(calls, "git", "commit", false));
        Assert.False(MachineReads.AllowRead(calls, "git", "pr", false));
        Assert.False(MachineReads.AllowRead(calls, "git", "status", true));
        Assert.False(MachineReads.AllowRead(calls, "git", "missing", false));
    }
    [Theory]
    [InlineData("status", CallKind.Read)]
    [InlineData("history", CallKind.Read)]
    [InlineData("operation_host", CallKind.Read)]
    [InlineData("prFile", CallKind.BulkRead)]
    [InlineData("commit", CallKind.Write)]
    [InlineData("switchBranch", CallKind.Write)]
    [InlineData("start", CallKind.Write)]
    [InlineData("cancel", CallKind.Write)]
    public void Named_git_methods_keep_their_reviewed_kind(string method, CallKind kind) =>
        Assert.Equal(kind, GitCallKinds.KindOf(method));
    [Theory]
    [InlineData("push")]
    [InlineData("review")]
    [InlineData("arbitrary")]
    public void Network_actions_cannot_be_invented_as_direct_socket_methods(string method) =>
        Assert.Null(GitCallKinds.KindOf(method));
}
