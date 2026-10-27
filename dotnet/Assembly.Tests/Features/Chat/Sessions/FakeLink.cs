using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Remote.Smoke;
using Assembly.Tests.Core.Acp;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Features.Chat.Sessions;

/// <summary>A scripted agent behind the real ACP client. The test plays the agent's side.</summary>
internal sealed class FakeLink : IAgentLink
{
    public const string AcpSessionId = "acp-1";

    private readonly FakeAgent _agent = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeLink() => Client = new AcpClient(_agent.ClientReads, _agent.ClientWrites, new AllowOncePolicy(), TranscriptLog.Discard());

    public AcpClient Client { get; }

    public Task Exited => _exited.Task;

    public bool Disposed { get; private set; }

    /// <summary>Answers <c>initialize</c>, then <see cref="OpenAsync"/>.</summary>
    public async Task<JsonElement> StartAsync(
        bool canResume = true, string acpSessionId = AcpSessionId, bool canSteer = true, bool canRename = false, string? configOptions = null)
    {
        var initialize = await ReadAsync("initialize");
        var sessions = canResume ? ""","sessionCapabilities":{"resume":{},"close":{}}""" : "";
        var steering = canSteer ? "\"steering\":{\"supported\":true}" : "";
        var rename = canRename ? "\"jetbrains\":{\"air\":{\"version\":1,\"capabilities\":[\"sessionRename\"]}}" : "";
        var meta = string.Join(',', new[] { steering, rename }.Where(part => part.Length > 0));
        await _agent.ReplyAsync(initialize, $$$"""{"protocolVersion":1,"agentCapabilities":{"loadSession":true{{{sessions}}}},"authMethods":[],"_meta":{{{{meta}}}}}""");
        return await OpenAsync(acpSessionId, configOptions);
    }

    /// <summary>
    /// Answers the next <c>session/new</c> (with <paramref name="acpSessionId"/>) or <c>session/resume</c>, with
    /// <paramref name="configOptions"/> if given, and returns the request.
    /// </summary>
    public async Task<JsonElement> OpenAsync(string acpSessionId = AcpSessionId, string? configOptions = null)
    {
        var open = await _agent.ReadAsync();
        var method = open.GetProperty("method").GetString();
        Assert.Contains(method, new[] { "session/new", "session/resume" });
        var options = configOptions is null ? "" : $""","configOptions":{configOptions}""";
        await _agent.ReplyAsync(open, method == "session/new" ? $$"""{"sessionId":"{{acpSessionId}}"{{options}}}""" : $$"""{{{options.TrimStart(',')}}}""");
        return open;
    }

    /// <summary>Answers the next <c>session/close</c>, and returns the session it closed.</summary>
    public async Task<string> CloseAsync()
    {
        var close = await ReadAsync("session/close");
        await _agent.ReplyAsync(close, "{}");
        return close.GetProperty("params").GetProperty("sessionId").GetString()!;
    }

    public async Task<JsonElement> ReadAsync(string method)
    {
        var message = await _agent.ReadAsync();
        Assert.Equal(method, message.GetProperty("method").GetString());
        return message;
    }

    public Task UpdateAsync(string updateJson, string acpSessionId = AcpSessionId) => _agent.SendAsync(Update(updateJson, acpSessionId));

    public Task TextAsync(string text, string messageId = "m1", string acpSessionId = AcpSessionId) =>
        UpdateAsync(Text(text, messageId), acpSessionId);

    /// <summary>Answers <paramref name="request"/> and sends a text in the same write, so the client reads both at once.</summary>
    public Task ReplyThenTextAsync(JsonElement request, string resultJson, string text, string messageId) =>
        _agent.SendAsync($$"""{"jsonrpc":"2.0","id":{{request.GetProperty("id").GetRawText()}},"result":{{resultJson}}}""" + "\n" + Update(Text(text, messageId), AcpSessionId));

    private static string Update(string updateJson, string acpSessionId) =>
        $$$"""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"{{{acpSessionId}}}","update":{{{updateJson}}}}}""";

    private static string Text(string text, string messageId) =>
        $$$"""{"sessionUpdate":"agent_message_chunk","messageId":"{{{messageId}}}","content":{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}}}""";

    public Task ReplyAsync(JsonElement request, string resultJson) => _agent.ReplyAsync(request, resultJson);

    /// <summary>The agent asks the client (<c>session/request_permission</c>, <c>elicitation/create</c>) as request <paramref name="id"/>.</summary>
    public Task RequestAsync(int id, string method, string paramsJson) =>
        _agent.SendAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{paramsJson}}}}""");

    public Task<JsonElement> NextMessageAsync() => _agent.ReadAsync();

    /// <summary>The client's answer to the agent's request: the next message, which must be a result.</summary>
    public async Task<JsonElement> ResultAsync()
    {
        var message = await _agent.ReadAsync();
        Assert.False(message.TryGetProperty("method", out _), $"expected a result, got {message}");
        return message.GetProperty("result");
    }

    public Task ReplyErrorAsync(JsonElement request, int code, string message) => _agent.ReplyErrorAsync(request, code, message);

    /// <summary>The process dies: its output closes and it exits.</summary>
    public void Crash()
    {
        if (_exited.TrySetResult())
            _agent.CloseOutput();
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        Crash();
        await Client.DisposeAsync();
    }
}
