using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Notion;

public sealed class NotionCache
{
    private readonly IAppDatabase _db;
    private readonly NotionSettings _settings;
    private const string Columns = "source_id,page_id,title,key_number,task_key,project_id,project_title,status,priority,assignee,due,url,fetched_ms";
    public NotionCache(IAppDatabase db, NotionSettings settings)
    {
        _db = db; _settings = settings;
        db.Writer.Write("notion.cacheSchema", s => s.Execute("""
            CREATE TABLE IF NOT EXISTS notion_tasks(source_id TEXT NOT NULL,page_id TEXT NOT NULL,title TEXT NOT NULL,key_number INTEGER NOT NULL,task_key TEXT NOT NULL,
              project_id TEXT NOT NULL,project_title TEXT NOT NULL,status TEXT NOT NULL,priority TEXT NOT NULL,assignee TEXT NOT NULL,due TEXT,url TEXT NOT NULL,fetched_ms INTEGER NOT NULL,PRIMARY KEY(source_id,page_id));
            CREATE INDEX IF NOT EXISTS notion_task_filters ON notion_tasks(source_id,project_id,status,priority,key_number,page_id);
            CREATE INDEX IF NOT EXISTS notion_task_titles ON notion_tasks(source_id,title,page_id);
            CREATE TABLE IF NOT EXISTS notion_cache_meta(source_id TEXT PRIMARY KEY,fetched_ms INTEGER NOT NULL,truncated INTEGER NOT NULL,revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS notion_project_map(source_id TEXT NOT NULL,notion_project_id TEXT NOT NULL,project_id TEXT NOT NULL,project_title TEXT NOT NULL,PRIMARY KEY(source_id,notion_project_id));
            CREATE TABLE IF NOT EXISTS preferences(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            """));
    }
    public NotionTaskPage Query(NotionQuery query, CancellationToken ct)
    {
        query.Validate();
        using var snapshot = _db.OpenSnapshot(ct);
        var connection = snapshot.QueryFirst("SELECT data_source_id,generation FROM notion_connection WHERE singleton=1", r => (Source: r.Text(0), Generation: r.Long(1)));
        var source = connection.Source;
        var conditions = "source_id=$source";
        var parameters = new List<SqlArg> { new("$source", source), new("$limit", query.Limit + 1), new("$offset", query.Offset) };
        if (query.Search is { Length: > 0 } search)
        {
            conditions += " AND (title LIKE $search ESCAPE '\\' OR task_key LIKE $search ESCAPE '\\' OR project_title LIKE $search ESCAPE '\\' OR status LIKE $search ESCAPE '\\')";
            parameters.Add(new("$search", "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }
        if (query.ProjectId is { } project)
        {
            conditions += " AND project_id=$project"; parameters.Add(new("$project", project));
        }
        AddIn("status", query.Statuses, ref conditions, parameters);
        AddIn("priority", query.Priorities, ref conditions, parameters);
        var sort = query.Sort == "number" ? "key_number" : "title COLLATE NOCASE";
        var direction = query.Direction == "asc" ? "ASC" : "DESC";
        var rows = snapshot.Query($"SELECT {Columns} FROM notion_tasks WHERE {conditions} ORDER BY {sort} {direction},page_id {direction} LIMIT $limit OFFSET $offset", ReadRow, parameters.ToArray());
        var more = rows.Count > query.Limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var meta = snapshot.QueryFirst("SELECT fetched_ms,truncated,revision FROM notion_cache_meta WHERE source_id=$source", r => (Fetched: r.Long(0), Truncated: r.Long(1) != 0, Revision: r.Long(2)), new SqlArg("$source", source));
        var statuses = snapshot.Query("SELECT DISTINCT status FROM notion_tasks WHERE source_id=$source ORDER BY status", r => r.Text(0), new SqlArg("$source", source));
        var priorities = snapshot.Query("SELECT DISTINCT priority FROM notion_tasks WHERE source_id=$source ORDER BY priority", r => r.Text(0), new SqlArg("$source", source));
        var projects = snapshot.Query("SELECT DISTINCT project_id,project_title FROM notion_tasks WHERE source_id=$source AND project_id<>'' ORDER BY project_title COLLATE NOCASE,project_id", r => new NotionProjectOption(r.Text(0), r.Text(1)), new SqlArg("$source", source));
        var result = new NotionTaskPage(rows, more, statuses, priorities, projects, meta.Fetched, meta.Truncated, connection.Generation, meta.Revision);
        if (JsonSerializer.SerializeToUtf8Bytes(result, NotionJson.Default.NotionTaskPage).Length > 256 * 1024 - 1024) throw new NotionException("notion_too_large");
        return result;
    }
    private static void AddIn(string column, string[]? values, ref string conditions, List<SqlArg> parameters)
    {
        if (values is not { Length: > 0 }) return;
        conditions += " AND " + column + " IN (SELECT value FROM json_each($" + column + "))";
        parameters.Add(new("$" + column, JsonSerializer.Serialize(values, NotionJson.Default.StringArray)));
    }
    public NotionRefreshReceipt Replace(IWriteScope scope, long generation, IReadOnlyList<NotionTaskRow> rows, long fetchedMs, bool truncated)
    {
        var connection = scope.QueryFirst("SELECT data_source_id,generation,secret_account FROM notion_connection WHERE singleton=1", r => (Source: r.Text(0), Generation: r.Long(1), HasToken: !r.IsNull(2)));
        if (connection.Generation != generation || !connection.HasToken) throw new NotionException("notion_connection_changed");
        if (rows.Count > 500 || rows.Any(x => x.SourceId != connection.Source)) throw new NotionException("notion_source_mismatch");
        scope.Execute("DELETE FROM notion_tasks WHERE source_id=$source", new SqlArg("$source", connection.Source));
        foreach (var r in rows)
            scope.Execute($"INSERT INTO notion_tasks({Columns}) VALUES($s,$p,$t,$n,$k,$pi,$pt,$st,$pr,$a,$d,$u,$f)",
                new("$s", r.SourceId), new("$p", r.PageId), new("$t", r.Title), new("$n", r.KeyNumber), new("$k", r.TaskKey), new("$pi", r.ProjectId), new("$pt", r.ProjectTitle),
                new("$st", r.Status), new("$pr", r.Priority), new("$a", r.Assignee), new("$d", r.Due), new("$u", r.Url), new("$f", fetchedMs));
        scope.Execute("INSERT INTO notion_cache_meta(source_id,fetched_ms,truncated,revision) VALUES($s,$f,$t,1) ON CONFLICT(source_id) DO UPDATE SET fetched_ms=$f,truncated=$t,revision=revision+1", new("$s", connection.Source), new("$f", fetchedMs), new("$t", truncated ? 1 : 0));
        Publish(scope, connection.Source, generation);
        return new(rows.Count, fetchedMs, truncated, generation);
    }
    public static void Publish(IWriteScope scope, string source, long generation)
    {
        var revision = scope.QueryFirst("SELECT revision FROM notion_cache_meta WHERE source_id=$s", r => r.Long(0), new SqlArg("$s", source));
        scope.Publish("notion-tasks", null, new NotionRevision(generation, revision), NotionJson.Default.NotionRevision);
    }
    public (string Id, string Title) Project(string source, string relation, CancellationToken ct) =>
        _db.QueryFirst("SELECT project_id,project_title FROM notion_project_map WHERE source_id=$s AND notion_project_id=$p", r => (r.Text(0), r.Text(1)), ct, new("$s", source), new("$p", relation));
    public NotionRefreshReceipt Commit(long generation, IReadOnlyList<NotionTaskRow> rows, long fetchedMs, bool truncated) => _db.Writer.Write("notion.refresh", s => Replace(s, generation, rows, fetchedMs, truncated));
    public NotionQuery View()
    {
        var query = _db.QueryFirst("SELECT value FROM preferences WHERE key='notion.tasks.view'", r => JsonSerializer.Deserialize(r.Text(0), NotionJson.Default.NotionQuery), default) ?? new();
        return query with { Statuses = query.Statuses ?? [], Priorities = query.Priorities ?? [] };
    }
    public NotionQuery SaveView(IWriteScope scope, NotionQuery query)
    {
        query.Validate();
        query = query with { Statuses = query.Statuses ?? [], Priorities = query.Priorities ?? [] };
        scope.Execute("INSERT INTO preferences(key,value) VALUES('notion.tasks.view',$v) ON CONFLICT(key) DO UPDATE SET value=$v", new SqlArg("$v", JsonSerializer.Serialize(query, NotionJson.Default.NotionQuery)));
        return query;
    }
    private static NotionTaskRow ReadRow(IRow r) => new(r.Text(0), r.Text(1), r.Text(2), r.Long(3), r.Text(4), r.Text(5), r.Text(6), r.Text(7), r.Text(8), r.Text(9), r.IsNull(10) ? null : r.Text(10), r.Text(11), r.Long(12));
}
