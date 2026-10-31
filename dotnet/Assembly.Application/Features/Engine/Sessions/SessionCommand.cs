using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Sessions;

public abstract record SessionCommand : EngineCommand;
public sealed record StartSession(string Provider, string Model, string Folder, string Title,
    string? ParentId, string? RunId, string? TaskUrl) : SessionCommand;
public sealed record SetSessionState(string State, string? ProviderSessionId, string? Reason) : SessionCommand;
public sealed record StopSession() : SessionCommand;
