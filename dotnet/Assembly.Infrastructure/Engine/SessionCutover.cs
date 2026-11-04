using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Acp;
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
using Assembly.Infrastructure.Engine.Helpers;

namespace Assembly.Infrastructure.Engine;

/// <summary>Imports and projects owning chat sessions in the Engine's single initialization transaction.</summary>
public sealed class SessionCutover(IAppDatabase db, SessionStore sessions, IAgentLauncher launcher, string checkouts,
    TimeProvider time, Func<string, string?>? inheritedEnvironment = null, EngineHelperCards? helperCards = null,
    IProjectRoots? projectRoots = null, IProjectUse? projectUse = null, string? owningHome = null) : IEngineSessions
{
    private EngineCommands? _commands;
    private string? _machine;

    internal ISessionProjectProjection ProjectProjection => sessions;

    public void EnsureSchema(IWriteScope scope)
    {
        SessionStore.EnsureEngineProjectionSchema(scope);
        SetBareFolder(scope);
    }

    private void SetBareFolder(IWriteScope scope) => scope.Execute(
        "UPDATE engine_meta SET bare_folder=$folder WHERE singleton=1", new SqlArg("$folder", BareSessionFolder.ForHome(owningHome)));

    public string AccountContext(string configuredAgentId) =>
        launcher.Agents.FirstOrDefault(agent => agent.Id == configuredAgentId) is { } descriptor
            ? Application.Common.Agents.AccountContext.Of(descriptor, inheritedEnvironment ?? Environment.GetEnvironmentVariable) : "";

    private ImportedSession Metadata(IReadScope scope, SessionInfo session, string? folder = null)
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
        folder ??= SessionStore.Folder(checkouts, sessions.Top(scope, session))
            ?? throw new CallFailed("This session has no working folder.");
        return new(session.SessionId, session.ParentSessionId, session.Agent, model, folder, session.Title ?? "", AccountContext(session.Agent));
    }

    public void Initialize(IWriteScope scope, EngineCommands commands, string machine, long nowMs, bool legacyV4Upgrade = false)
    {
        SetBareFolder(scope);
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

    public void ProjectStart(IWriteScope scope, CommandContext context, StartSession command) =>
        ProjectStart(scope, context, command, null);

    private void ProjectStart(IWriteScope scope, CommandContext context, StartSession command, SessionCreationPreparation? preparation)
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
        // MCP starts project their chat row here; create already inserted it on this same writer.
        var checkout = scope.QueryFirst("SELECT project_id,folder,branch FROM engine_session WHERE id=$id",
            row => (Project: row.IsNull(0) ? null : row.Text(0), Folder: row.Text(1), Branch: row.IsNull(2) ? null : row.Text(2)),
            new SqlArg("$id", context.EntityId));
        if (checkout.Project is not null)
        {
            if (preparation is not null)
                ValidatePreparation(scope, context.EntityId, checkout.Project, command.ParentId, checkout.Folder, preparation);
            else
            {
                var project = projectRoots?.Find(scope, checkout.Project);
                if (project is null || project.Removed || project.Machine != "local")
                    throw new CallFailed("The selected project is unavailable on this owner.", "wrong_machine");
                if (projectUse is null) throw new CallFailed("Project usage is unavailable.", "configuration_unavailable");
                projectUse.Touched(scope, checkout.Project);
            }
            sessions.Assigned(scope, context.EntityId, checkout.Project);
            sessions.CheckoutChanged(scope, context.EntityId, checkout.Folder, checkout.Branch);
        }
        helperCards?.Observe(scope, context.EntityId);
    }

    private void Project(IWriteScope scope, string id, string? sourceRepo = null)
    {
        if (sessions.Get(scope, id) is not null)
        {
            sessions.ProjectBare(scope, id);
            return;
        }
        var row = scope.QueryFirst("""
            SELECT provider,folder,CAST(title AS BLOB),parent_id,provider_session_id,updated_ms,model FROM engine_session WHERE id=$id
            """, r => new
        {
            Provider = r.Text(0),
            Folder = r.Text(1),
            Title = new System.Text.UTF8Encoding(false, true).GetString(r.Blob(2)),
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
        sessions.ProjectBare(scope, id);
    }

    public void Attach(EngineCommands commands, string machine)
    {
        _commands = commands; _machine = machine;
        sessions.BindEngine(this);
    }

    public void Started(IWriteScope scope, SessionInfo session, string? initiatingSessionId,
        string? projectId = null, CheckoutIntent? checkout = null, SessionCreationPreparation? preparation = null)
    {
        if (session.WorkingFolder is not null)
        {
            SessionLocation? parentLocation = null;
            if (session.ParentSessionId is not null)
            {
                parentLocation = Location(scope, session.ParentSessionId);
                if (parentLocation is null || !parentLocation.Ready)
                    throw new CallFailed("The parent's checkout is not ready.", "checkout_not_ready");
            }
            var folder = parentLocation?.Folder ?? preparation?.CanonicalRoot ?? BareSessionFolder.ForHome(owningHome);
            if (preparation is not null)
                ValidatePreparation(scope, session.SessionId, projectId, session.ParentSessionId, session.WorkingFolder, preparation);
            if (projectId is not null && preparation is null)
            {
                var project = projectRoots?.Find(scope, projectId);
                if (project is null || project.Removed || project.Machine != "local")
                    throw new CallFailed("The selected project is unavailable on this owner.", "wrong_machine");
                if (parentLocation is null) folder = project.RootPath;
            }
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || session.WorkingFolder != folder)
                throw new CallFailed("The owning folder is unavailable.", "configuration_unavailable");
            var config = sessions.ConfigForEngineInitialization(scope, session.SessionId);
            var captured = config.ConfigTarget is null ? null
                : ((AgentDescriptor Descriptor, IReadOnlyDictionary<string, string> Environment, string TargetId)?)
                    ConfigTargetIdentity.Capture(launcher, session.Agent, config.ConfigTarget);
            if (projectId is null && parentLocation is null && preparation is null)
                Directory.CreateDirectory(folder);
            launcher.ValidateFolder(folder);
            if (captured is { } target)
                ConfigTargetIdentity.Validate(launcher, target.Descriptor, target.Environment);
            Apply(scope, session.SessionId, 0, 0, initiatingSessionId is null
                ? new(ActorKind.Window, "owner") : new(ActorKind.Session, initiatingSessionId),
                new StartSession(session.Agent, Metadata(scope, session, folder).Model, folder, session.Title ?? "", session.ParentSessionId,
                    null, null, ManagedCheckout: false, ProjectId: projectId, Checkout: checkout), preparation: preparation);
            return;
        }
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

    public void Titled(IWriteScope scope, string sessionId, string actualStoredTitle, ReadOnlyMemory<byte> exactTitleUtf8)
    {
        var row = Owning(scope, sessionId);
        if (row is null || row.Title == actualStoredTitle) return;
        Apply(scope, sessionId, row.Version, row.Epoch, new(ActorKind.System, "session.titled"), new ReportSessionTitle(actualStoredTitle), exactTitleUtf8);
        helperCards?.Observe(scope, sessionId);
    }

    private void ValidatePreparation(IReadScope scope, string sessionId, string? projectId, string? parentId,
        string folder, SessionCreationPreparation preparation)
    {
        var client = scope.QueryFirst("SELECT client_id FROM session_info WHERE session_id=$id", row => row.Text(0), new SqlArg("$id", sessionId));
        if (preparation.TargetMachine != _machine || string.IsNullOrWhiteSpace(preparation.FromMachine) ||
            preparation.FromMachine == preparation.TargetMachine || preparation.ClientId != client ||
            preparation.ProjectId != projectId || string.IsNullOrWhiteSpace(projectId) ||
            !Path.IsPathFullyQualified(preparation.CanonicalRoot))
            throw new CallFailed("The prepared project owner changed.", "source_changed");
        var expected = parentId is null ? preparation.CanonicalRoot : Location(scope, parentId)?.Folder;
        if (expected != folder) throw new CallFailed("The prepared project folder changed.", "source_changed");
    }

    private sealed record OwningSession(long Version, long Epoch, string? Model, string Title);
    private OwningSession? Owning(IReadScope scope, string id) => scope.QueryFirst("""
        SELECT version,epoch,model,CAST(title AS BLOB) FROM engine_session WHERE id=$id AND machine=$machine AND holder='local'
        """, r => new OwningSession(r.Long(0), r.Long(1), r.IsNull(2) ? null : r.Text(2), new System.Text.UTF8Encoding(false, true).GetString(r.Blob(3))),
        new("$id", id), new("$machine", _machine));

    public SessionLocation? Location(string sessionId)
    {
        using var scope = db.OpenSnapshot(CancellationToken.None);
        return Location(scope, sessionId);
    }

    public SessionLocation? Location(IReadScope scope, string sessionId) => scope.QueryFirst(
        """
        SELECT s.folder,s.managed_checkout,s.state,s.checkout_state,
            EXISTS(SELECT 1 FROM engine_session peer WHERE peer.machine=s.machine AND peer.holder='local'
                AND peer.folder=s.folder AND peer.checkout_state='pending')
        FROM engine_session s WHERE s.id=$id AND s.machine=$machine AND s.holder='local'
        """,
        r => new SessionLocation(r.Text(0), r.Long(1) == 1,
            r.Text(2) != "starting" && (r.IsNull(3) || r.Text(3) is not ("pending" or "refused")) && r.Long(4) == 0), new("$id", sessionId), new("$machine", _machine));

    public string? Folder(string sessionId) => Location(sessionId)?.Folder;
    public string? Folder(IReadScope scope, string sessionId) => Location(scope, sessionId)?.Folder;

    private void Apply(IWriteScope scope, string id, long version, long epoch, EngineActor actor, EngineCommand command, ReadOnlyMemory<byte>? exactTitleUtf8 = null, SessionCreationPreparation? preparation = null)
    {
        var context = new CommandContext(Guid.NewGuid(), _machine!, actor, id, version, epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
        var result = command is StartSession or ReportSessionTitle
            ? _commands!.ApplyPreparedSessionTitle(scope, context, PreparedSessionTitleCommand.Prepare((SessionCommand)command, exactTitleUtf8),
                projectStart: preparation is null ? null : (writer, startContext, start) => ProjectStart(writer, startContext, start, preparation))
            : _commands!.Apply(scope, context, command);
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
