namespace Assembly.Engine.Acp.Messages;

/// <summary><c>_session/rename</c>, from the AIR session index: sets the session's title, and the agent then makes none of its own.</summary>
public sealed record RenameSessionRequest(string SessionId, string Title);

public sealed record RenameSessionResponse;
