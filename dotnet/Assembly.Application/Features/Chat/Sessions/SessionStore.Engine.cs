using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Domain.Sessions;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionStore
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
        if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_config') WHERE name='pending_model'", r => r.Long(0)) == 0)
            scope.Execute("ALTER TABLE session_config ADD COLUMN pending_model TEXT NULL");
    }

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
