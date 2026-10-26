namespace Assembly.Application.Common.Acp.Messages;

/// <summary>Ends a session in the adapter, which stops that session's agent process. The adapter keeps running.</summary>
public sealed record CloseSessionRequest(string SessionId);
