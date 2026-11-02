namespace Assembly.Application.Common.Acp.Messages;

public sealed record InitializeRequest(int ProtocolVersion, Implementation Info, ClientCapabilities Capabilities)
{
    public static InitializeRequest ForAssembly(string name, string version) =>
        new(AcpProtocol.Version, new Implementation(name, version), ClientCapabilities.Assembly);
}
