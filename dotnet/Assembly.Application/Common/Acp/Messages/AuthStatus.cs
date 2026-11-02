namespace Assembly.Application.Common.Acp.Messages;

public sealed record AuthStatus(string Kind, string? Label);
public sealed record AuthStatusNotification(AuthStatus AuthStatus);
