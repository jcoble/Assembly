namespace Assembly.Application.Common.Acp.Messages;

public sealed record InitializeRequest(int ProtocolVersion, ClientCapabilities ClientCapabilities, Implementation ClientInfo);
