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

    public void Assigned(IWriteScope scope, string sessionId, string? projectId)
    {
        if (scope.Execute("UPDATE session_info SET project_id=$project WHERE session_id=$id AND (project_id IS NULL OR project_id=$project)",
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
    internal void ProjectEngineSession(IWriteScope scope, SessionInfo session, string? pendingModel, string folder)
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
}
