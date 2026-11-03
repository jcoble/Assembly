using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Workspaces;

/// <summary>Desktop view snapshots. The supplied writer owns revision comparison, replacement and publication together.</summary>
public sealed class WorkspaceStore
{
    public const int MaxSnapshotBytes = 3 * 1024 * 1024;
    private readonly IAppDatabase db;
    private readonly TimeProvider clock;
    private const string Where = "machine=$machine AND session_id=$session AND checkout_root=$root";
    public WorkspaceStore(IAppDatabase db, TimeProvider clock)
    {
        this.db = db; this.clock = clock;
        db.Writer.Write("workspaces.schema", scope => scope.Execute(WorkspacesSchema.Create));
    }
    public WorkspaceReadResult Read(WorkspaceKey key, CancellationToken ct)
    {
        Validate(key);
        return db.QueryFirst($"SELECT snapshot,revision FROM workspace_snapshot WHERE {Where}", row =>
        {
            using var snapshot = JsonDocument.Parse(row.Text(0));
            return new WorkspaceReadResult(snapshot.RootElement.Clone(), row.Long(1));
        }, ct, Args(key)) ?? new WorkspaceReadResult(null, 0);
    }
    public WorkspaceChange? Change(WorkspaceKey key, CancellationToken ct)
    {
        Validate(key);
        return db.QueryFirst($"SELECT revision FROM workspace_snapshot WHERE {Where}",
            row => new WorkspaceChange(key.Machine, key.SessionId, key.CheckoutRoot, row.Long(0)), ct, Args(key));
    }
    public WorkspaceSaveResult Save(IWriteScope scope, WorkspaceKey key, long expectedRevision, JsonElement snapshot)
    {
        Validate(key);
        if (expectedRevision < 0 || snapshot.ValueKind == JsonValueKind.Undefined) throw new CallBadArguments("A workspace save needs a snapshot and a nonnegative revision.");
        var json = snapshot.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes) throw new CallFailed("This workspace is larger than 3 MiB, so it was not saved.");
        var revision = scope.QueryFirst($"SELECT revision FROM workspace_snapshot WHERE {Where}", row => row.Long(0), Args(key));
        if (revision != expectedRevision) throw new CallFailed("This workspace changed elsewhere; it was not saved.");
        if (revision == long.MaxValue) throw new CallFailed("This workspace revision cannot be advanced.");
        var version = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("version", out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : 0;
        var next = revision + 1;
        scope.Execute("""
            INSERT INTO workspace_snapshot(machine,session_id,checkout_root,schema_version,revision,updated_at,snapshot)
            VALUES($machine,$session,$root,$version,$revision,$updated,$snapshot)
            ON CONFLICT(machine,session_id,checkout_root) DO UPDATE SET schema_version=excluded.schema_version,
                revision=excluded.revision,updated_at=excluded.updated_at,snapshot=excluded.snapshot
            """, new SqlArg("$machine", key.Machine), new SqlArg("$session", key.SessionId), new SqlArg("$root", key.CheckoutRoot),
            new SqlArg("$version", version), new SqlArg("$revision", next), new SqlArg("$updated", clock.GetUtcNow().ToUnixTimeMilliseconds()), new SqlArg("$snapshot", json));
        scope.Publish("workspaces", key.TopicKey, new WorkspaceChange(key.Machine, key.SessionId, key.CheckoutRoot, next), WorkspacesJson.Default.WorkspaceChange, keyedOnly: true);
        return new WorkspaceSaveResult(next);
    }
    private static SqlArg[] Args(WorkspaceKey key) => [new("$machine", key.Machine), new("$session", key.SessionId), new("$root", key.CheckoutRoot)];
    private static void Validate(WorkspaceKey key)
    {
        if (string.IsNullOrEmpty(key.Machine) || key.Machine.Contains('/') || key.Machine.Contains('\0')
            || string.IsNullOrEmpty(key.SessionId) || key.SessionId.Contains('/') || key.SessionId.Contains('\0')
            || string.IsNullOrEmpty(key.CheckoutRoot) || key.CheckoutRoot.Contains('\0'))
            throw new CallBadArguments("A workspace needs its source machine, session and checkout root.");
    }
}
