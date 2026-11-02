using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task Terminal_detail_round_trip_preserves_bounded_preview_source_count_and_exit_state()
    {
        var session = await CreateSessionAsync();
        await SendAsync(session);
        var agent = await _boxLauncher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"in_progress","content":[{"type":"terminal","terminalId":"T"}]}""");
        var block = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string('a', 65536)));
        for (var i = 0; i < 17; i++)
            await agent.UpdateAsync("{\"sessionUpdate\":\"terminal_output_chunk\",\"terminalId\":\"T\",\"data\":\"" + block + "\"}");
        await agent.UpdateAsync("""{"sessionUpdate":"terminal_update","terminalId":"T","exitStatus":{"exitCode":-1,"signal":"SIGTERM"}}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"completed","content":[]}""");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        var result = await GetJsonAsync(_desktop!, DesktopToken, $"/sessions/{session}/tools/C");
        Assert.Equal(1048576, System.Text.Encoding.UTF8.GetByteCount(result.GetProperty("output").GetString()!));
        Assert.Equal(1114112, result.GetProperty("outputBytes").GetInt64());
        Assert.True(result.GetProperty("outputTruncated").GetBoolean());
        var terminal = Assert.Single(result.GetProperty("terminals").EnumerateArray());
        Assert.Equal(FakeLink.AcpSessionId, terminal.GetProperty("acpSessionId").GetString());
        Assert.Equal("T", terminal.GetProperty("terminalId").GetString());
        Assert.Equal(-1, terminal.GetProperty("exitStatus").GetProperty("exitCode").GetInt64());
        Assert.Equal("SIGTERM", terminal.GetProperty("exitStatus").GetProperty("signal").GetString());
        Assert.Equal(0, _desktopLauncher.Launches);
    }
}
