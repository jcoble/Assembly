using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Contracts;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Sessions;

public abstract record SessionCommand : EngineCommand;
public sealed record StartSession(string Provider, string? Model, string Folder, string Title,
    string? ParentId, string? RunId, string? TaskUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RepoUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ManagedCheckout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckoutIntent? Checkout = null) : SessionCommand;
public sealed record RegisterForeignSession(string Machine, string Provider, string Title) : SessionCommand;
public sealed record SetSessionState(string State, string? ProviderSessionId, string? Reason) : SessionCommand;
public sealed record StopSession() : SessionCommand;

public sealed record LinkTask(string TaskUrl) : SessionCommand;

public sealed record ReportSessionModel(string? Model) : SessionCommand;
/// <summary>The branch git really has checked out in the session's folder, seen after something moved it. Null when detached.</summary>
public sealed record ReportSessionBranch(string? Branch) : SessionCommand;
public sealed record ReportSessionTitle(string Title) : SessionCommand;

public sealed record AssignProject(string ProjectId) : SessionCommand;

public sealed record AdoptSession(string Provider, string? Model, string Folder, string Title,
    string ProviderSessionId, string FromMachine, string FromSessionId, long Epoch,
    string? RepoUrl = null) : SessionCommand;

/// <summary>A versioned owning-machine checkout operation; it performs no Git work in the writer.</summary>
public sealed record ChangeCheckout(string CheckoutKind, string? Branch, string? Base,
    string? Folder, bool CreateBranch = false, string? ExpectedHead = null) : SessionCommand;
