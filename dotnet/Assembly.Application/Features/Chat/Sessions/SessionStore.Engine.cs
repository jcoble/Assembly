using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Domain.Sessions;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionStore : ISessionProjectProjection
{
    private IEngineSessions? _engine;

    public void BindEngine(IEngineSessions engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Interlocked.CompareExchange(ref _engine, engine, null) is not null)
            throw new InvalidOperationException("The session store is already bound to an Engine.");
    }

    public static void EnsureEngineProjectionSchema(IWriteScope scope)
    {
        EnsureProjectProjectionSchema(scope);
        if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_config') WHERE name='pending_model'", r => r.Long(0)) == 0)
            scope.Execute("ALTER TABLE session_config ADD COLUMN pending_model TEXT NULL");
    }

    private static void EnsureProjectProjectionSchema(IWriteScope scope)
    {
        if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name='is_bare'", r => r.Long(0)) == 0)
            scope.Execute("ALTER TABLE session_info ADD COLUMN is_bare INTEGER NOT NULL DEFAULT 0");
        foreach (var column in new[] { "project_id", "checkout_root", "branch", "client_id" })
            if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name=$name", r => r.Long(0), new SqlArg("$name", column)) == 0)
                scope.Execute($"ALTER TABLE session_info ADD COLUMN {column} TEXT NULL");
        scope.Execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS session_client_id_unique ON session_info(client_id) WHERE client_id IS NOT NULL;
            CREATE TRIGGER IF NOT EXISTS session_project_once
            BEFORE UPDATE OF project_id ON session_info
            WHEN OLD.project_id IS NOT NULL AND NEW.project_id IS NOT OLD.project_id
            BEGIN SELECT RAISE(ABORT, 'a session''s project never changes'); END;
            """);
    }

    public void ProjectBare(IWriteScope scope, string sessionId)
    {
        var bare = BareSessionFolder.Predicate("e.project_id", "e.folder");
        if (scope.Execute($"""
            UPDATE session_info SET is_bare=EXISTS(SELECT 1 FROM engine_session e WHERE e.id=$id AND {bare})
            WHERE session_id=$id AND COALESCE(machine,'')=''
              AND is_bare<>EXISTS(SELECT 1 FROM engine_session e WHERE e.id=$id AND {bare})
            """, new SqlArg("$id", sessionId)) > 0)
            Publish(scope, sessionId);
    }

    public void ProjectRemoteBare(IWriteScope scope, string machine, string route)
    {
        var changed = scope.Query($"""
            WITH owner_sessions AS (
              SELECT r.entity_id AS id,json_extract(r.row_json,'$.provider') AS provider,
                json_extract(r.row_json,'$.parentId') AS parent,
                json_extract(r.row_json,'$.isBare')=1 AND json_extract(r.row_json,'$.projectId') IS NULL AS bare
              FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation
              WHERE m.machine=$machine AND m.state='live' AND r.entity_kind='session'
                AND json_type(r.row_json,'$.isBare') IN ('true','false')
                AND json_extract(r.row_json,'$.id')=r.entity_id AND json_extract(r.row_json,'$.machine')=r.machine)
            UPDATE session_info AS s SET is_bare=CASE WHEN s.project_id IS NULL THEN owner.bare ELSE 0 END
            FROM owner_sessions owner WHERE s.session_id=owner.id AND s.machine=$route AND s.agent=owner.provider
              AND s.parent_session_id IS owner.parent AND s.is_bare<>CASE WHEN s.project_id IS NULL THEN owner.bare ELSE 0 END
              AND NOT EXISTS(SELECT 1 FROM engine_session other WHERE other.id=s.session_id AND other.machine<>$machine)
            RETURNING {Columns}
            """, ReadSession, new SqlArg("$machine", machine), new("$route", route));
        foreach (var session in changed)
            Publish(scope, session, SessionItem.From(session, _summaries?.Get(session.SessionId) ?? SessionSummary.None, null));
    }

    public void Assigned(IWriteScope scope, string sessionId, string? projectId)
    {
        if (scope.Execute("UPDATE session_info SET project_id=$project,is_bare=CASE WHEN $project IS NULL THEN is_bare ELSE 0 END WHERE session_id=$id AND (project_id IS NULL OR project_id=$project)",
            new SqlArg("$id", sessionId), new SqlArg("$project", projectId)) != 1)
            throw new InvalidOperationException("The session project projection was refused.");
        Publish(scope, sessionId);
    }

    public void SetCheckout(IWriteScope scope, string sessionId, string checkoutRoot, string? branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutRoot);
        if (scope.Execute("UPDATE session_info SET checkout_root=$root,branch=$branch WHERE session_id=$id",
            new SqlArg("$id", sessionId), new SqlArg("$root", checkoutRoot), new SqlArg("$branch", branch)) != 1)
            throw new InvalidOperationException("The session checkout projection was refused.");
        Publish(scope, sessionId);
    }

    public void CheckoutChanged(IWriteScope scope, string sessionId, string folder, string? branch) => SetCheckout(scope, sessionId, folder, branch);

    public SessionInfo? Get(IReadScope scope, string sessionId) =>
        scope.QueryFirst($"SELECT {Columns} FROM session_info WHERE session_id=$id", ReadSession, new SqlArg("$id", sessionId));

    public IReadOnlyList<SessionInfo> LocalOwning(IReadScope scope) =>
        scope.Query($"SELECT {Columns} FROM session_info WHERE COALESCE(machine,'')='' AND (parent_session_id IS NULL OR helper=1) ORDER BY created_at,session_id", ReadSession);

    private void CreateOwned(SessionInfo session, string? initiatingSessionId, string sql, params (string Name, object? Value)[] parameters) =>
        CreateOwned(session, initiatingSessionId, null, null, sql, parameters);

    private void CreateOwned(SessionInfo session, string? initiatingSessionId, Action<IWriteScope>? initialize, Action? validate, string sql,
        params (string Name, object? Value)[] parameters) =>
        _writer.Write("sessions", scope =>
        {
            validate?.Invoke();
            var changed = scope.Execute(sql, Args(parameters));
            initialize?.Invoke(scope);
            if (string.IsNullOrEmpty(session.Machine)) _engine?.Started(scope, session, initiatingSessionId);
            Publish(scope, session.SessionId);
            return changed;
        });

    public void ProjectEngineSession(IWriteScope scope, SessionInfo session, string? pendingModel) =>
        ProjectEngineSession(scope, session, pendingModel, _engine?.Folder(scope, session.SessionId)
            ?? throw new InvalidOperationException("The owning Engine folder is required for projection."));

    // Initialization has not attached the observer; the Engine supplies its authoritative folder in this transaction.
    public void ProjectEngineSession(IWriteScope scope, SessionInfo session, string? pendingModel, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var inserted = scope.Execute("""
            INSERT INTO session_info(session_id,agent,repo_url,title,acp_session_id,created_at,updated_at,machine,parent_session_id,helper)
            VALUES($id,$agent,$repo,CAST($title AS TEXT),$acp,$now,$now,$machine,$parent,$helper)
            ON CONFLICT(session_id) DO NOTHING;
            """, new("$id", session.SessionId), new("$agent", session.Agent), new("$repo", session.RepoUrl), new("$title", session.Title is null ? null : TitleUtf8.GetBytes(session.Title)),
            new("$acp", session.AcpSessionId), new("$now", session.UpdatedAt), new("$machine", session.Machine),
            new("$parent", session.ParentSessionId), new("$helper", session.Helper ? 1 : 0));
        if (inserted == 0) return;
        if (pendingModel is not null)
            scope.Execute("""
                INSERT INTO session_config(session_id,pending_model) VALUES($id,$model)
                ON CONFLICT(session_id) DO UPDATE SET pending_model=COALESCE(session_config.pending_model,excluded.pending_model);
                """, new("$id", session.SessionId), new("$model", pendingModel));
        Publish(scope, session, SessionItem.From(session, _summaries?.Get(session.SessionId) ?? SessionSummary.None, folder));
    }

    public static void ValidateCreateClientId(string clientId)
    {
        if (clientId is null || clientId.Length is < 1 or > 64 ||
            clientId.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new CallBadArguments("clientId must contain 1–64 ASCII letters, digits, underscore or dash.");
    }

    public SessionInfo? ByClient(IReadScope scope, string clientId, string? owningMachine = null)
    {
        ValidateCreateClientId(clientId);
        var session = scope.QueryFirst($"SELECT {Columns} FROM session_info WHERE client_id=$client", ReadSession,
            new SqlArg("$client", clientId));
        if (session is not null && (session.Machine is not null || scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE id=$id AND holder='local'
                  AND machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
                  AND ($owner IS NULL OR machine=$owner)
                """, row => row.Long(0), new SqlArg("$id", session.SessionId), new("$owner", owningMachine)) != 1))
            throw new CallFailed("The accepted session is no longer owned here.", "source_changed");
        return session;
    }

    private static readonly string ClientCorrelation = $"""
        SELECT s.session_id,
            CASE WHEN e.checkout_operation_id=initial.operation THEN e.checkout_state ELSE NULL END,e.state,
            CASE WHEN COALESCE(s.machine,'')='' AND e.holder='local'
                AND e.machine=(SELECT machine_id FROM engine_meta WHERE singleton=1) AND e.machine=$owner
                AND e.provider=s.agent AND e.parent_id IS s.parent_session_id THEN 1 ELSE 0 END,
            initial.operation,e.project_id,initial.root,e.machine,e.provider,e.parent_id,e.folder,e.managed_checkout,s.repo_url,
            {BareSessionFolder.Predicate("e.project_id", "e.folder")}
        FROM session_info s LEFT JOIN engine_session e ON e.id=s.session_id
        LEFT JOIN (
            SELECT accepted.id AS session_id,json_extract(accepted.checkout_intent_json,'$.initialOperationId') AS operation,
                CASE WHEN json_extract(effect.payload_json,'$.initialStart.parentId') IS NULL
                    THEN json_extract(effect.payload_json,'$.initialStart.folder') ELSE NULL END AS root
            FROM engine_session accepted JOIN engine_outbox effect
                ON effect.id='checkout/'||json_extract(accepted.checkout_intent_json,'$.initialOperationId')
            WHERE accepted.id=(SELECT session_id FROM session_info WHERE client_id=$client)
              AND json_valid(accepted.checkout_intent_json)
              AND json_type(accepted.checkout_intent_json,'$.initialOperationId')='text'
              AND EXISTS(SELECT 1 FROM engine_command_receipt receipt
                WHERE receipt.command_id=replace(json_extract(accepted.checkout_intent_json,'$.initialOperationId'),'-','')
                  AND json_extract(receipt.result_json,'$.applied')=1)
              AND effect.kind='checkout' AND effect.session_id=accepted.id AND effect.machine=accepted.machine
              AND json_extract(effect.payload_json,'$.operationId')=json_extract(accepted.checkout_intent_json,'$.initialOperationId')
              AND json_extract(effect.payload_json,'$.initialOperationId')=json_extract(accepted.checkout_intent_json,'$.initialOperationId')
              AND json_type(effect.payload_json,'$.initialStart')='object'

        ) initial ON initial.session_id=e.id
        WHERE s.client_id=$client
        """;

    private static SessionsFeature.ByClientReply ReadCorrelation(IRow row)
    {
        if (row.Long(3) != 1) throw new CallFailed("The accepted session is no longer owned here.", "source_changed");
        var initial = row.IsNull(4) ? null : row.Text(4);
        if (initial is not null && !Guid.TryParseExact(initial, "D", out _))
            throw new CallFailed("The initial checkout evidence is unavailable.", "configuration_unavailable");
        var root = row.IsNull(6) ? null : row.Text(6);
        if (root is not null && !Path.IsPathFullyQualified(root)) root = null;
        var facts = new SessionsFeature.CreatedSessionFacts(row.Text(7), row.Text(8), row.IsNull(9) ? null : row.Text(9),
            row.Text(10), row.Long(11) != 0, row.Text(12), row.Long(13) == 1);
        if (string.IsNullOrWhiteSpace(facts.Machine) || string.IsNullOrWhiteSpace(facts.Agent) ||
            !Path.IsPathFullyQualified(facts.Folder) ||
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(facts, SessionsJson.Default.CreatedSessionFacts).Length > 64 * 1024)
            throw new CallFailed("The accepted session metadata is unavailable.", "configuration_unavailable");
        return new(row.Text(0), row.IsNull(1) ? null : row.Text(1), row.Text(2), initial, row.IsNull(5) ? null : row.Text(5), root, facts);
    }

    internal SessionsFeature.ByClientReply FindByClient(string clientId, CancellationToken cancellation, string owningMachine)
    {
        ValidateCreateClientId(clientId);
        return _db.QueryFirst(ClientCorrelation, ReadCorrelation, cancellation, new SqlArg("$client", clientId), new("$owner", owningMachine))
            ?? new(null, null, null);
    }

    internal SessionsFeature.ByClientReply FindByClient(IReadScope scope, string clientId, string owningMachine)
    {
        ValidateCreateClientId(clientId);
        return scope.QueryFirst(ClientCorrelation, ReadCorrelation, new SqlArg("$client", clientId), new("$owner", owningMachine))
            ?? new(null, null, null);
    }


    internal SessionLocation? OwningLocation(IReadScope scope, string sessionId) => _engine?.Location(scope, sessionId);

    internal SessionInfo AcceptCopiedCreation(string sessionId, SessionsFeature.CreatedSessionFacts facts, string route,
        string? projectId, Action<IWriteScope> validate, Action<IWriteScope>? accepted = null,
        Action<IWriteScope>? establishOwner = null) => _writer.Write("sessions.accepted", scope =>
    {
        validate(scope);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(route) ||
            string.IsNullOrWhiteSpace(facts.Machine) || string.IsNullOrWhiteSpace(facts.Agent) || !Path.IsPathFullyQualified(facts.Folder))
            throw new CallFailed("The accepted owner metadata is unavailable.", "source_changed");
        establishOwner?.Invoke(scope);
        validate(scope);
        if (scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE id=$id AND
                    (holder='local' OR machine<>$owner OR provider<>$agent OR parent_id IS NOT $parent)
                """, row => row.Long(0), new SqlArg("$id", sessionId), new("$owner", facts.Machine),
                new("$agent", facts.Agent), new("$parent", facts.ParentSessionId)) != 0)
            throw new CallFailed("The accepted session identity collides with another owner.", "source_changed");
        if (Get(scope, sessionId) is { } existing)
        {
            if (existing.Machine != route || existing.Agent != facts.Agent || existing.ParentSessionId != facts.ParentSessionId ||
                existing.ProjectId is not null && existing.ProjectId != projectId)
                throw new CallFailed("The copied session identity changed.", "source_changed");
            accepted?.Invoke(scope);
            validate(scope);
            var isBare = existing.ProjectId is null && projectId is null && facts.IsBare;
            if (existing.IsBare != isBare)
            {
                scope.Execute("UPDATE session_info SET is_bare=$bare WHERE session_id=$id", new SqlArg("$bare", isBare ? 1 : 0), new("$id", sessionId));
                Publish(scope, sessionId);
            }
            return existing with { IsBare = isBare };
        }
        var session = new SessionInfo(sessionId, facts.Agent, facts.RepoUrl, null, null, Now(), route,
            ParentSessionId: facts.ParentSessionId, Helper: facts.ParentSessionId is not null, WorkingFolder: facts.Folder, ProjectId: projectId, IsBare: projectId is null && facts.IsBare);
        scope.Execute("""
            INSERT INTO session_info(session_id,agent,repo_url,created_at,updated_at,machine,parent_session_id,helper,working_folder,project_id,is_bare)
            VALUES($id,$agent,$repo,$now,$now,$machine,$parent,$helper,$folder,$project,$bare)
            """, new("$id", sessionId), new("$agent", facts.Agent), new("$repo", facts.RepoUrl), new("$now", session.UpdatedAt),
            new("$machine", route), new("$parent", facts.ParentSessionId), new("$helper", session.Helper ? 1 : 0), new("$folder", facts.Folder), new("$project", projectId), new("$bare", session.IsBare ? 1 : 0));
        accepted?.Invoke(scope);
        validate(scope);
        Publish(scope, session.SessionId);
        return session;
    });

    public SessionInfo Create(IWriteScope scope, string agent, string clientId, string folder,
        string? projectId, CheckoutIntent? checkout, string? parentSessionId = null, string? title = null,
        string? initiatingSessionId = null, InitialSessionConfig? initialConfig = null, SessionCreationPreparation? preparation = null)
    {
        ValidateCreateClientId(clientId);
        if (ByClient(scope, clientId) is { } accepted) return accepted;
        if (_engine is null) throw new CallFailed("An owning Engine is required.", "configuration_unavailable");
        if (string.IsNullOrWhiteSpace(agent) || !Path.IsPathFullyQualified(folder))
            throw new CallBadArguments("An agent and owning folder are required.");
        if (parentSessionId is not null)
        {
            var parent = Get(scope, parentSessionId);
            if (parent is null || parent.Machine is not null)
                throw new CallFailed("The parent is not owned here.", "wrong_machine");
            if (projectId is not null && projectId != parent.ProjectId)
                throw new CallBadArguments("A child inherits its parent's project.");
            projectId = parent.ProjectId;
            var location = _engine.Location(scope, parentSessionId);
            if (location is null || !location.Ready)
                throw new CallFailed("The parent's checkout is not ready.", "checkout_not_ready");
            if (folder != location.Folder)
                throw new CallFailed("The parent's owning folder changed.", "source_changed");
        }
        if (initialConfig is not null && (string.IsNullOrEmpty(initialConfig.TargetId) || initialConfig.Choices is null ||
            initialConfig.Choices.Count == 0 || initialConfig.Choices.Any(choice => string.IsNullOrEmpty(choice.Key) || choice.Value is null)))
            throw new CallBadArguments("Select the agent settings again.");
        var session = new SessionInfo(Guid.NewGuid().ToString(), agent, folder, title, null, Now(),
            ParentSessionId: parentSessionId, Helper: parentSessionId is not null,
            WorkingFolder: folder, ProjectId: projectId);
        scope.Execute("""
            INSERT INTO session_info(session_id,agent,repo_url,title,created_at,updated_at,parent_session_id,helper,
                working_folder,project_id,client_id)
            VALUES($id,$agent,$folder,CAST($title AS TEXT),$now,$now,$parent,$helper,$folder,$project,$client)
            """, new("$id", session.SessionId), new("$agent", agent), new("$folder", folder),
            new("$title", title is null ? null : TitleUtf8.GetBytes(title)), new("$now", session.UpdatedAt),
            new("$parent", parentSessionId), new("$helper", parentSessionId is null ? 0 : 1),
            new("$project", projectId), new("$client", clientId));
        if (initialConfig is not null)
            scope.Execute("INSERT INTO session_config(session_id,choices,config_target) VALUES($id,$choices,$target)",
                new("$id", session.SessionId), new("$choices", System.Text.Json.JsonSerializer.Serialize(initialConfig.Choices,
                    SessionsJson.Default.IReadOnlyDictionaryStringString)), new("$target", initialConfig.TargetId));
        _engine.Started(scope, session, initiatingSessionId, projectId, checkout, preparation);
        Publish(scope, session.SessionId);
        return Get(scope, session.SessionId)!;
    }
}
