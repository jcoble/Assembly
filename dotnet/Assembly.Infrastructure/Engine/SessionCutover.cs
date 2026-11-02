using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

/// <summary>Imports and projects owning chat sessions in the Engine's single initialization transaction.</summary>
public sealed class SessionCutover(IAppDatabase db, SessionStore sessions, IAgentLauncher launcher, string checkouts,
    TimeProvider time, Func<string, string?>? inheritedEnvironment = null) : IEngineSessions
{
    private EngineCommands? _commands;
    private string? _machine;

    public void EnsureSchema(IWriteScope scope) => SessionStore.EnsureEngineProjectionSchema(scope);

    public string AccountContext(string configuredAgentId) =>
        launcher.Agents.FirstOrDefault(agent => agent.Id == configuredAgentId) is { } descriptor
            ? Application.Common.Agents.AccountContext.Of(descriptor, inheritedEnvironment ?? Environment.GetEnvironmentVariable) : "";

    private ImportedSession Metadata(IReadScope scope, SessionInfo session)
    {
        var config = sessions.ConfigForEngineInitialization(scope, session.SessionId);
        var optionsJson = config.Options ?? sessions.AgentConfig(scope, session.Agent);
        string? model = null;
        if (optionsJson is not null)
        {
            using var options = JsonDocument.Parse(optionsJson);
            model = ConfigOptions.ModelValue(options.RootElement);
            if (options.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var option in options.RootElement.EnumerateArray())
                {
                    if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("category", out var category) ||
                        category.ValueKind != JsonValueKind.String || category.GetString() != "model") continue;
                    if (!option.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    var select = ConfigOptions.Selects(options.RootElement).FirstOrDefault(value => value.Id == id.GetString());
                    if (select.Values is not null && config.Choices.TryGetValue(select.Id, out var chosen) &&
                        !string.IsNullOrWhiteSpace(chosen) && select.Values.Contains(chosen)) model = chosen;
                }
        }
        var folder = SessionStore.Folder(checkouts, sessions.Top(scope, session))
            ?? throw new CallFailed("This session has no working folder.");
        return new(session.SessionId, session.ParentSessionId, session.Agent, model, folder, session.Title ?? "", AccountContext(session.Agent));
    }

    public void Initialize(IWriteScope scope, EngineCommands commands, string machine, long nowMs, bool legacyV4Upgrade = false)
    {
        var legacy = sessions.LocalOwning(scope);
        if (legacyV4Upgrade)
            foreach (var session in legacy)
                scope.Execute("""
                    UPDATE engine_session SET managed_checkout=1 WHERE id=$id AND machine=$machine AND holder='local' AND folder=$folder
                    """, new("$id", session.SessionId), new("$machine", machine),
                    new("$folder", SessionStore.Folder(checkouts, sessions.Top(scope, session))));
        SessionImport.Import(scope, machine, nowMs, legacy.Select(session => Metadata(scope, session)).ToArray());
        foreach (var row in scope.Query("""
            SELECT id FROM engine_session WHERE machine=$machine AND holder='local' ORDER BY created_ms,id
            """, r => r.Text(0), new SqlArg("$machine", machine)))
            Project(scope, row);
        Recover(scope, commands, machine, nowMs);
    }

    public void ProjectStart(IWriteScope scope, CommandContext context, StartSession command)
    {
        if (context.Actor.Kind == ActorKind.System && context.Actor.Id.StartsWith("machine:", StringComparison.Ordinal))
        {
            var source = context.Actor.Id[8..];
            if (source == context.Machine || command.ParentId is null || scope.QueryFirst("""
                    SELECT COUNT(*) FROM engine_session WHERE id=$parent AND state='foreign' AND machine=$source AND machine<>$local
                    """, r => r.Long(0), new("$parent", command.ParentId), new("$source", source), new("$local", context.Machine)) != 1)
                throw new CallFailed("The peer parent does not belong to the source machine.");
            if (command.ManagedCheckout == true && (string.IsNullOrWhiteSpace(command.RepoUrl) ||
                command.Folder != SessionStore.Folder(checkouts, new SessionInfo(command.ParentId, "", "", null, null, 0))))
                throw new CallFailed("The managed peer location contradicts the target checkout.");
            if (command.ManagedCheckout != true && command.RepoUrl is not null)
                throw new CallFailed("A direct peer folder cannot override its repository.");
        }
        Project(scope, context.EntityId, command.RepoUrl);
    }

    private void Project(IWriteScope scope, string id, string? sourceRepo = null)
    {
        if (sessions.Get(scope, id) is not null) return;
        var row = scope.QueryFirst("""
            SELECT provider,folder,title,parent_id,provider_session_id,updated_ms,model FROM engine_session WHERE id=$id
            """, r => new
        {
            Provider = r.Text(0),
            Folder = r.Text(1),
            Title = r.Text(2),
            Parent = r.IsNull(3) ? null : r.Text(3),
            Acp = r.IsNull(4) ? null : r.Text(4),
            Updated = r.Long(5),
            Model = r.IsNull(6) ? null : r.Text(6)
        }, new SqlArg("$id", id))!;
        var foreignParent = row.Parent is { } parentId && scope.QueryFirst(
            "SELECT COUNT(*) FROM engine_session WHERE id=$id AND state='foreign' AND machine<>(SELECT machine_id FROM engine_meta WHERE singleton=1)",
            r => r.Long(0), new SqlArg("$id", parentId)) == 1;
        if (!foreignParent && row.Parent is { } localParent && sessions.Get(scope, localParent) is null) Project(scope, localParent);
        var repo = sourceRepo ?? (foreignParent ? row.Folder : row.Parent is { } parent ? sessions.Get(scope, parent)?.RepoUrl
            ?? throw new InvalidOperationException("The parent requires its trusted repository projection.") : row.Folder);
        sessions.ProjectEngineSession(scope, new(id, row.Provider, repo, row.Title == "" && row.Parent is null ? null : row.Title, row.Acp, row.Updated,
            ParentSessionId: row.Parent, Helper: row.Parent is not null), row.Model, row.Folder);
    }

    public void Attach(EngineCommands commands, string machine)
    {
        _commands = commands; _machine = machine;
        sessions.BindEngine(this);
    }

    public void Started(IWriteScope scope, SessionInfo session, string? initiatingSessionId)
    {
        var metadata = Metadata(scope, session);
        Apply(scope, session.SessionId, 0, 0, initiatingSessionId is null ? new(ActorKind.Window, "owner") : new(ActorKind.Session, initiatingSessionId),
            new StartSession(metadata.Provider, metadata.Model, metadata.Folder, metadata.Title, metadata.ParentId, null, null, session.RepoUrl, true));
    }

    public void Configured(IWriteScope scope, string sessionId, string? model)
    {
        var row = Owning(scope, sessionId);
        if (row is null || row.Model == model) return;
        Apply(scope, sessionId, row.Version, row.Epoch, new(ActorKind.System, "session.configured"), new ReportSessionModel(model));
    }

    public void Titled(IWriteScope scope, string sessionId, string actualStoredTitle)
    {
        var row = Owning(scope, sessionId);
        if (row is null || row.Title == actualStoredTitle) return;
        Apply(scope, sessionId, row.Version, row.Epoch, new(ActorKind.System, "session.titled"), new ReportSessionTitle(actualStoredTitle));
    }

    private sealed record OwningSession(long Version, long Epoch, string? Model, string Title);
    private OwningSession? Owning(IReadScope scope, string id) => scope.QueryFirst("""
        SELECT version,epoch,model,title FROM engine_session WHERE id=$id AND machine=$machine AND holder='local'
        """, r => new OwningSession(r.Long(0), r.Long(1), r.IsNull(2) ? null : r.Text(2), r.Text(3)),
        new("$id", id), new("$machine", _machine));

    public SessionLocation? Location(string sessionId)
    {
        using var scope = db.OpenSnapshot(CancellationToken.None);
        return Location(scope, sessionId);
    }

    public SessionLocation? Location(IReadScope scope, string sessionId) => scope.QueryFirst(
        "SELECT folder,managed_checkout FROM engine_session WHERE id=$id AND machine=$machine AND holder='local'",
        r => new SessionLocation(r.Text(0), r.Long(1) == 1), new("$id", sessionId), new("$machine", _machine));

    public string? Folder(string sessionId) => Location(sessionId)?.Folder;
    public string? Folder(IReadScope scope, string sessionId) => Location(scope, sessionId)?.Folder;

    private void Apply(IWriteScope scope, string id, long version, long epoch, EngineActor actor, EngineCommand command)
    {
        var result = _commands!.Apply(scope, new(Guid.NewGuid(), _machine!, actor, id, version, epoch, time.GetUtcNow().ToUnixTimeMilliseconds()), command);
        if (!result.Applied) throw new CallFailed($"The session operation was refused: {result.Error}.");
    }

    private static void Recover(IWriteScope scope, EngineCommands commands, string machine, long now)
    {
        foreach (var need in scope.Query("""
            SELECT n.id,n.version,s.epoch FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id
            WHERE n.state='open' AND n.provider_request_id IS NOT NULL AND s.machine=$machine AND s.holder='local'
            """, r => (Id: r.Text(0), Version: r.Long(1), Epoch: r.Long(2)), new SqlArg("$machine", machine)))
            Require(commands.Apply(scope, new(Guid.NewGuid(), machine, new(ActorKind.System, "session.restart"), need.Id, need.Version, need.Epoch, now), new ResolveNeed("cancelled", null, "provider_gone")));
        foreach (var session in scope.Query("""
            SELECT id,version,epoch FROM engine_session WHERE machine=$machine AND holder='local'
              AND (state='running' OR state='needs_me' AND activity_state='running')
            """, r => (Id: r.Text(0), Version: r.Long(1), Epoch: r.Long(2)), new SqlArg("$machine", machine)))
            Require(commands.Apply(scope, new(Guid.NewGuid(), machine, new(ActorKind.System, "session.restart"), session.Id, session.Version, session.Epoch, now), new SetSessionState("idle", null, "backend_restart")));
    }

    private static void Require(CommandResult result)
    {
        if (!result.Applied) throw new CallFailed($"Session recovery was refused: {result.Error}.");
    }
}
