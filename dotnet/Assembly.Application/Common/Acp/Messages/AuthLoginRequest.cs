namespace Assembly.Application.Common.Acp.Messages;

public sealed record AuthLoginRequest(string MethodId);
public sealed record AuthLogoutRequest;
public sealed record AuthResponse;
internal sealed record AuthCancelRequest(long RequestId);
