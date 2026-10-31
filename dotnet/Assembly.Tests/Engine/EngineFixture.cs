using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Engine;

internal sealed class EngineFixture : IDisposable
{
    private readonly TempDatabase _file = new();
    public AppDatabase Db { get; private set; }
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
    public string Machine { get; private set; }
    public int ChildLimit { get; }

    public EngineFixture(int childLimit = 5)
    {
        ChildLimit = childLimit;
        Db = OpenDatabase();
        Machine = EngineSchema.Open(Db, Time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private AppDatabase OpenDatabase()
    {
        var db = AppDatabase.Open(_file.Path, Time, new global::Assembly.Infrastructure.Topics.Topics());
        RowStore.Open(db);
        return db;
    }

    public void Reopen()
    {
        Db.Dispose();
        Db = OpenDatabase();
        Machine = EngineSchema.Open(Db, Time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    public void SeedSession(string id = "s", string state = "idle", string? parentId = null, string? runId = null,
        string provider = "codex", string holder = "local", long epoch = 0)
    {
        Db.Writer.Write("test.seed-session", s => s.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                activity_state,parent_id,run_id,holder,epoch,cancel_requested)
            VALUES($id,0,$machine,$now,$now,$provider,'model','/test','Test',$state,'idle',$parent,$run,$holder,$epoch,0)
            """, new("$id", id), new("$machine", Machine), new("$now", Time.GetUtcNow().ToUnixTimeMilliseconds()),
            new("$provider", provider), new("$state", state), new("$parent", parentId), new("$run", runId),
            new("$holder", holder), new("$epoch", epoch)));
    }

    public void SeedRun(string id = "r", string ownerSessionId = "s", string state = "pending", long ownerLossLimitMs = 60000)
    {
        Db.Writer.Write("test.seed-run", s =>
        {
            s.Execute("""
                INSERT INTO engine_run(id,version,machine,created_ms,updated_ms,owner_session_id,state,owner_loss_limit_ms)
                VALUES($id,0,$machine,$now,$now,$owner,$state,$limit)
                """, new("$id", id), new("$machine", Machine), new("$now", Time.GetUtcNow().ToUnixTimeMilliseconds()),
                new("$owner", ownerSessionId), new("$state", state), new("$limit", ownerLossLimitMs));
            return s.Execute("UPDATE engine_session SET run_id=$run WHERE id=$owner",
                new("$run", id), new("$owner", ownerSessionId));
        });
    }

    public void SeedStep(string id = "step", string runId = "r", string state = "pending", string? sessionId = null,
        int attempt = 0, int maxAttempts = 1, bool required = true, bool ruleSkippable = false)
    {
        Db.Writer.Write("test.seed-step", s => s.Execute("""
            INSERT INTO engine_step(id,version,machine,created_ms,updated_ms,run_id,name,session_id,state,attempt,
                max_attempts,required,rule_skippable)
            VALUES($id,0,$machine,$now,$now,$run,$id,$session,$state,$attempt,$max,$required,$skippable)
            """, new("$id", id), new("$machine", Machine), new("$now", Time.GetUtcNow().ToUnixTimeMilliseconds()),
            new("$run", runId), new("$session", sessionId), new("$state", state), new("$attempt", attempt),
            new("$max", maxAttempts), new("$required", required ? 1 : 0), new("$skippable", ruleSkippable ? 1 : 0)));
    }

    public string? State(string table, string id) => Text($"SELECT state FROM {table} WHERE id=$id", new SqlArg("$id", id));
    public long Number(string sql, params SqlArg[] args) => Db.QueryFirst(sql, r => r.Long(0), default, args);
    public string? Text(string sql, params SqlArg[] args) => Db.QueryFirst(sql, r => r.IsNull(0) ? null : r.Text(0), default, args);
    public long Count(string table) => Number($"SELECT COUNT(*) FROM {table}");

    public IReadOnlyList<EngineEvent> Events() => Db.Query("""
        SELECT machine,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json FROM engine_event ORDER BY seq
        """, r => new EngineEvent(r.Text(0), r.Long(1), r.Text(2), r.Text(3), r.Text(4), r.Long(5), r.Long(6),
            r.Long(7) != 0, JsonSerializer.Deserialize(r.Text(8), EngineJson.Default.EngineEventData)!), default);

    public void Dispose()
    {
        Db.Dispose();
        _file.Dispose();
    }
}
