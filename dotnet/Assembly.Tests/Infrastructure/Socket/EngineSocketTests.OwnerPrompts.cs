using Assembly.Engine.Messages;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Owner_prompt_cannot_bypass_the_existing_public_window_allowlist()
    {
        _f.SeedSession();
        using var socket = await Open();
        var result = await Command(socket, new SendOwnerPrompt("s", "Exact text", "C", [], null), "prompt/s/C");
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, _f.Count("engine_message")); Assert.Equal(0, _f.Count("engine_outbox"));
        Assert.Equal(0, _f.Count("engine_command_receipt")); Assert.Empty(_f.Events());
    }
}
