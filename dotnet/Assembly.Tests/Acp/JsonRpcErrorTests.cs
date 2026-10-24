using System.Text.Json;
using Assembly.Engine.Acp.JsonRpc;

namespace Assembly.Tests.Acp;

public sealed class JsonRpcErrorTests
{
    [Fact]
    public void An_error_keeps_the_details_the_agent_sends()
    {
        using var error = JsonDocument.Parse("""{"code":-32603,"message":"Internal error","data":{"details":"Session not found"}}""");
        var exception = JsonRpcException.FromError(error.RootElement);
        Assert.Equal(-32603, exception.Code);
        Assert.Equal("Internal error: Session not found", exception.Message);
    }

    [Fact]
    public void An_error_without_details_is_just_its_message()
    {
        using var error = JsonDocument.Parse("""{"code":-32601,"message":"Method not found"}""");
        Assert.Equal("Method not found", JsonRpcException.FromError(error.RootElement).Message);
    }
}
