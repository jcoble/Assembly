namespace Assembly.Engine.Acp.Messages;

/// <summary>Sets one of the session's <c>configOptions</c>, for example <c>effort</c> or <c>model</c>, to one of its values.</summary>
public sealed record SetConfigOptionRequest(string SessionId, string ConfigId, string Value);
