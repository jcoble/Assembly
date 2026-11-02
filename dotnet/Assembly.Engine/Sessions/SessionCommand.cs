using Assembly.Engine.Contracts;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Sessions;

public abstract record SessionCommand : EngineCommand;
public sealed record StartSession(string Provider, string? Model, string Folder, string Title,
    string? ParentId, string? RunId, string? TaskUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RepoUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ManagedCheckout = null) : SessionCommand;
public sealed record RegisterForeignSession(string Machine, string Provider, string Title) : SessionCommand;
public sealed record SetSessionState(string State, string? ProviderSessionId, string? Reason) : SessionCommand;
public sealed record StopSession() : SessionCommand;

public sealed record LinkTask(string TaskUrl) : SessionCommand;

public sealed record ReportSessionModel(string? Model) : SessionCommand;
public sealed record ReportSessionTitle(string Title) : SessionCommand;

public sealed record AssignProject(string ProjectId) : SessionCommand;
