using System.Text.Json;

namespace Assembly.Application.Common.Acp;

public sealed record AcpError(int Code, string Message, JsonElement? Data = null)
{
    public bool IsAuthRequired => Code == -32000;
}
