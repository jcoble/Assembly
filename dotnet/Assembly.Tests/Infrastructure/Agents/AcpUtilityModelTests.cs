using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Preferences;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class AcpUtilityModelTests
{
    [Fact]
    public async Task A_disposable_configured_acp_session_returns_only_its_completed_answer()
    {
        using var db = new TempDatabase();
        using var database = TestDatabase.Open(db.Path);
        var preferences = new PreferenceStore(database);
        var wire = new FakeAgent();
        FakeLink? owned = null;
        string? cwd = null;
        var model = new AcpUtilityModel([new("configured", "Configured", "adapter-command", ["--acp"], null)], () => preferences.Read("utilityModel"),
            TimeProvider.System, TextWriter.Null, (command, permissions) =>
            {
                Assert.Equal("adapter-command", command.FileName);
                cwd = command.WorkingDirectory;
                Assert.Empty(Directory.EnumerateFileSystemEntries(cwd));
                return owned = new(wire, permissions);
            });
        var completing = model.CompleteAsync("Name this change", default);
        var initialize = await wire.ReadAsync();
        Assert.Equal("initialize", initialize.GetProperty("method").GetString());
        var capabilities = initialize.GetProperty("params").GetProperty("capabilities");
        Assert.False(capabilities.TryGetProperty("fs", out _));
        Assert.False(capabilities.TryGetProperty("terminal", out _));
        await wire.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{},"authMethods":[]}""");
        var create = await wire.ReadAsync();
        Assert.Equal("session/new", create.GetProperty("method").GetString());
        Assert.Equal(cwd, create.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Empty(create.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
        await wire.ReplyAsync(create, """{"sessionId":"utility","configOptions":[]}""");
        var prompt = await wire.ReadAsync();
        Assert.Equal("session/prompt", prompt.GetProperty("method").GetString());
        await wire.ReplyAsync(prompt, """{"messageId":"input"}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"user_message_chunk","messageId":"input","content":{"type":"text","text":"Draft"}}}}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"state_update","state":"running"}}}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"agent_message_chunk","messageId":"answer","content":{"type":"text","text":"Old"}}}}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"agent_message","messageId":"answer","content":[{"type":"text","text":"Exact answer"}]}}}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}}}""");
        var close = await wire.ReadAsync();
        Assert.Equal("session/close", close.GetProperty("method").GetString());
        await wire.ReplyAsync(close, "{}");
        Assert.Equal(new UtilityResult(UtilityStatus.Succeeded, "Exact answer", null), await completing);
        Assert.True(owned!.Disposed);
        Assert.False(Directory.Exists(cwd));
    }

    [Fact]
    public async Task Explicit_unconfigured_selection_never_falls_back_to_another_descriptor()
    {
        using var db = new TempDatabase();
        using var database = TestDatabase.Open(db.Path);
        var preferences = new PreferenceStore(database);
        using var setting = System.Text.Json.JsonDocument.Parse("""{"utilityModel":"missing"}""");
        preferences.Save(setting.RootElement);
        var model = new AcpUtilityModel([new("available", "Available", "unused", [], null)], () => preferences.Read("utilityModel"),
            TimeProvider.System, TextWriter.Null, (_, _) => throw new InvalidOperationException("Must not launch"));
        Assert.Equal(new UtilityResult(UtilityStatus.Unavailable, null, "utility_unconfigured"), await model.CompleteAsync("Draft", default));
    }

    [Fact]
    public async Task Authentication_required_is_unavailable_and_disposes_the_owned_attempt()
    {
        using var db = new TempDatabase();
        using var database = TestDatabase.Open(db.Path);
        var utilityPreferences = new PreferenceStore(database);
        var wire = new FakeAgent(); FakeLink? owned = null; string? cwd = null;
        var model = new AcpUtilityModel([new("configured", "Configured", "adapter", [], null)], () => utilityPreferences.Read("utilityModel"),
            TimeProvider.System, TextWriter.Null, (command, permissions) => { cwd = command.WorkingDirectory; return owned = new(wire, permissions); });
        var completing = model.CompleteAsync("Draft", default);
        await wire.ReplyAsync(await wire.ReadAsync(), """{"protocolVersion":2,"capabilities":{},"authMethods":[]}""");
        var create = await wire.ReadAsync();
        Assert.Equal("session/new", create.GetProperty("method").GetString());
        await wire.ReplyErrorAsync(create, -32000, "Sign in required");
        Assert.Equal(new UtilityResult(UtilityStatus.Unavailable, null, "utility_auth_required"), await completing);
        Assert.True(owned!.Disposed); Assert.False(Directory.Exists(cwd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancel_or_owned_deadline_cancels_closes_and_joins_the_exact_session(bool deadline)
    {
        using var db = new TempDatabase();
        using var database = TestDatabase.Open(db.Path);
        var utilityPreferences = new PreferenceStore(database);
        var wire = new FakeAgent(); FakeLink? owned = null; string? cwd = null;
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var cancelled = new CancellationTokenSource();
        var model = new AcpUtilityModel([new("configured", "Configured", "adapter", [], null)], () => utilityPreferences.Read("utilityModel"),
            time, TextWriter.Null, (command, permissions) => { cwd = command.WorkingDirectory; return owned = new(wire, permissions); });
        var completing = model.CompleteAsync("Draft", cancelled.Token);
        await wire.ReplyAsync(await wire.ReadAsync(), """{"protocolVersion":2,"capabilities":{},"authMethods":[]}""");
        await wire.ReplyAsync(await wire.ReadAsync(), """{"sessionId":"utility","configOptions":[]}""");
        var prompt = await wire.ReadAsync();
        Assert.Equal("session/prompt", prompt.GetProperty("method").GetString());
        await wire.ReplyAsync(prompt, """{"messageId":"input"}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"user_message_chunk","messageId":"input","content":{"type":"text","text":"Draft"}}}}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"state_update","state":"running"}}}""");
        if (deadline) time.Advance(TimeSpan.FromSeconds(120)); else cancelled.Cancel();
        var cancel = await wire.ReadAsync();
        Assert.Equal("session/cancel", cancel.GetProperty("method").GetString());
        Assert.Equal("utility", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}}}""");
        var close = await wire.ReadAsync();
        Assert.Equal("session/close", close.GetProperty("method").GetString());
        await wire.ReplyAsync(close, "{}");
        if (deadline) Assert.Equal(new UtilityResult(UtilityStatus.Failed, null, "utility_timeout"), await completing);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completing);
        Assert.True(owned!.Disposed); Assert.False(Directory.Exists(cwd));
    }

    [Fact]
    public async Task Utility_board_permission_is_denied_and_actual_tool_activity_fails_the_draft()
    {
        using var db = new TempDatabase();
        using var database = TestDatabase.Open(db.Path);
        var utilityPreferences = new PreferenceStore(database);
        var wire = new FakeAgent(); FakeLink? owned = null; string? cwd = null;
        var model = new AcpUtilityModel([new("configured", "Configured", "adapter", [], null)], () => utilityPreferences.Read("utilityModel"),
            TimeProvider.System, TextWriter.Null, (command, permissions) => { cwd = command.WorkingDirectory; return owned = new(wire, permissions); });
        var completing = model.CompleteAsync("Draft", default);
        await wire.ReplyAsync(await wire.ReadAsync(), """{"protocolVersion":2,"capabilities":{},"authMethods":[]}""");
        await wire.ReplyAsync(await wire.ReadAsync(), """{"sessionId":"utility","configOptions":[]}""");
        await wire.ReplyAsync(await wire.ReadAsync(), """{"messageId":"input"}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"user_message_chunk","messageId":"input","content":{"type":"text","text":"Draft"}}}}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"state_update","state":"running"}}}""");
        await wire.SendAsync("""{"jsonrpc":"2.0","id":91,"method":"session/request_permission","params":{"sessionId":"utility","title":"mcp__assembly__board_update","options":[{"optionId":"allow","name":"Allow once","kind":"allow_once"}]}}""");
        var permission = await wire.ReadAsync();
        Assert.Equal(91, permission.GetProperty("id").GetInt32());
        Assert.Equal("cancelled", permission.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"tool_call_update","toolCallId":"tool","status":"failed"}}}""");
        var cancel = await wire.ReadAsync();
        Assert.Equal("session/cancel", cancel.GetProperty("method").GetString());
        await wire.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"utility","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}}}""");
        var close = await wire.ReadAsync();
        Assert.Equal("session/close", close.GetProperty("method").GetString());
        await wire.ReplyAsync(close, "{}");
        Assert.Equal(new UtilityResult(UtilityStatus.Failed, null, "utility_used_tools"), await completing);
        Assert.True(owned!.Disposed); Assert.False(Directory.Exists(cwd));
    }

    private sealed class FakeLink : IAgentLink
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AcpClient Client { get; }
        public Task Exited => _exit.Task;
        public bool Disposed { get; private set; }
        public FakeLink(FakeAgent wire, IPermissionPolicy permissions) =>
            Client = new(wire.ClientReads, wire.ClientWrites, permissions, TranscriptLog.Discard());
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Disposed = true;
            _exit.TrySetResult();
        }
    }
}
