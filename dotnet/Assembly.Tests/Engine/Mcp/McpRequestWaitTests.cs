using Assembly.Engine.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Tests.Engine.Mcp;

public sealed class McpRequestWaitTests
{
    private const string Key = "assembly.internal.original-request-wait/v1";
    private const string Diagnostic = "The MCP request pipeline did not capture its original wait token.";

    [Fact]
    public async Task Missing_and_forged_values_are_internal_pipeline_errors_without_a_none_fallback()
    {
        await using var h = new Contexts();
        var request = h.Request();
        Assert.Equal(Diagnostic, Assert.Throws<InvalidOperationException>(() => McpInvocation.RequireOriginalWaitToken(request)).Message);
        foreach (var wrong in new object?[] { null, CancellationToken.None, "forged", new object() })
        {
            request.Items[Key] = wrong;
            Assert.Equal(Diagnostic, Assert.Throws<InvalidOperationException>(() => McpInvocation.RequireOriginalWaitToken(request)).Message);
        }
    }

    [Fact]
    public async Task Capture_returns_the_exact_original_token_and_removes_only_its_own_entry()
    {
        await using var h = new Contexts();
        using var original = new CancellationTokenSource();
        var request = h.Request();
        request.Items["other"] = "retained";
        var captured = McpRequestWait.Capture(request, original.Token);
        Assert.Equal(original.Token, McpInvocation.RequireOriginalWaitToken(request));
        Assert.True(McpInvocation.RequireOriginalWaitToken(request).CanBeCanceled);
        original.Cancel();
        Assert.True(McpInvocation.RequireOriginalWaitToken(request).IsCancellationRequested);
        captured.Dispose();
        captured.Dispose();
        Assert.False(request.Items.ContainsKey(Key));
        Assert.Equal("retained", request.Items["other"]);
        using (McpRequestWait.Capture(request, CancellationToken.None))
            Assert.Equal(CancellationToken.None, McpInvocation.RequireOriginalWaitToken(request));
        Assert.False(request.Items.ContainsKey(Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_null_or_arbitrary_item_is_restored_exactly(bool isNull)
    {
        await using var h = new Contexts();
        var request = h.Request();
        var previous = isNull ? null : new object();
        request.Items[Key] = previous;
        using (McpRequestWait.Capture(request, CancellationToken.None))
            Assert.Equal(CancellationToken.None, McpInvocation.RequireOriginalWaitToken(request));
        Assert.True(request.Items.ContainsKey(Key));
        Assert.Same(previous, request.Items[Key]);
    }

    [Fact]
    public async Task Nested_capture_restores_the_enclosing_original_then_its_prior_value()
    {
        await using var h = new Contexts();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var request = h.Request();
        var prior = new object();
        request.Items[Key] = prior;
        using (McpRequestWait.Capture(request, first.Token))
        {
            using (McpRequestWait.Capture(request, second.Token))
                Assert.Equal(second.Token, McpInvocation.RequireOriginalWaitToken(request));
            Assert.Equal(first.Token, McpInvocation.RequireOriginalWaitToken(request));
        }
        Assert.Same(prior, request.Items[Key]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_never_overwrites_a_downstream_replacement_or_recreates_a_removed_item(bool remove)
    {
        await using var h = new Contexts();
        var request = h.Request();
        var replacement = new object();
        using (McpRequestWait.Capture(request, CancellationToken.None))
        {
            if (remove) request.Items.Remove(Key);
            else request.Items[Key] = replacement;
        }
        if (remove) Assert.False(request.Items.ContainsKey(Key));
        else Assert.Same(replacement, request.Items[Key]);
    }

    [Fact]
    public async Task Separate_actual_request_contexts_hold_distinct_tokens_without_ambient_state()
    {
        await using var h = new Contexts();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = h.Request();
        var b = h.Request();
        Assert.NotSame(a.Items, b.Items);
        using (McpRequestWait.Capture(a, first.Token))
        using (McpRequestWait.Capture(b, second.Token))
        {
            first.Cancel();
            Assert.Equal(first.Token, McpInvocation.RequireOriginalWaitToken(a));
            Assert.Equal(second.Token, McpInvocation.RequireOriginalWaitToken(b));
            Assert.True(McpInvocation.RequireOriginalWaitToken(a).IsCancellationRequested);
            Assert.False(McpInvocation.RequireOriginalWaitToken(b).IsCancellationRequested);
        }
        Assert.False(a.Items.ContainsKey(Key));
        Assert.False(b.Items.ContainsKey(Key));
    }

    private sealed class Contexts : IAsyncDisposable
    {
        private readonly MemoryStream _input = new();
        private readonly MemoryStream _output = new();
        private readonly McpServer _server;

        public Contexts() => _server = McpServer.Create(new StreamServerTransport(_input, _output), new());
        public RequestContext<CallToolRequestParams> Request() => new(_server,
            new() { Method = "tools/call", Id = new RequestId(Guid.NewGuid().ToString()) }, new() { Name = "probe" });
        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            _input.Dispose();
            _output.Dispose();
        }
    }
}
