using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Security;

namespace Assembly.Application.Features.Notion;

public sealed class NotionSettings
{
    public const string Service = "Assembly Notion";
    private readonly IAppDatabase _db;
    private readonly ISecretStore _secrets;
    private readonly INotionHttp _http;
    private readonly SemaphoreSlim _lease = new(1);
    public NotionSettings(IAppDatabase db, ISecretStore secrets, INotionHttp http, TimeProvider time)
    {
        _http = http;
        _db = db;
        _secrets = secrets;
        db.Writer.Write("notion.schema", s => s.Execute("""
            CREATE TABLE IF NOT EXISTS notion_connection(singleton INTEGER PRIMARY KEY CHECK(singleton=1),data_source_id TEXT NOT NULL,secret_account TEXT,generation INTEGER NOT NULL,state TEXT NOT NULL);
            INSERT OR IGNORE INTO notion_connection(singleton,data_source_id,secret_account,generation,state) VALUES(1,'',NULL,0,'unconfigured');
            CREATE TABLE IF NOT EXISTS notion_secret_cleanup(account TEXT PRIMARY KEY);
            """));
    }
    public NotionSettingsView Read() => _db.QueryFirst("SELECT data_source_id,secret_account,generation,state FROM notion_connection WHERE singleton=1",
        r => new NotionSettingsView(r.Text(0), !r.IsNull(1), r.Long(2), r.Text(3), null), default)!;
    public NotionSettingsView Commit(Change change) => _db.Writer.Write("notion.oauthClaim", change.Commit);
    public async Task<NotionSettingsView> SaveAsync(string? dataSourceId, string? token, CancellationToken ct)
    {
        var change = await StageAsync(dataSourceId, token, ct);
        try { return _db.Writer.Write("notion.saveSettings", change.Commit); }
        finally { change.Dispose(); await ReconcileAsync(CancellationToken.None); }
    }
    public async Task<Change> StageAsync(string? source, string? token, CancellationToken ct)
    {
        var id = source is null ? null : NotionIds.Canonical(source);
        await _lease.WaitAsync(ct);
        var account = "integration-token/" + Guid.NewGuid().ToString("D");
        byte[]? bytes = null;
        try
        {
            _db.Writer.Write("notion.stageSecret", s => s.Execute("INSERT INTO notion_secret_cleanup(account) VALUES($a)", new SqlArg("$a", account)));
            bytes = token is null ? await _secrets.GetAsync(Service, "integration-token", ct) : Encoding.UTF8.GetBytes(token);
            if (bytes is null || bytes.Length == 0) throw new NotionException("missing_token");
            if (bytes.Length > 65536 || bytes.Any(b => b is < 0x21 or > 0x7e)) throw new NotionException("invalid_token");
            await _secrets.PutAsync(Service, account, bytes, ct);
            var read = await _secrets.GetAsync(Service, account, ct);
            try
            {
                if (read is null || !CryptographicOperations.FixedTimeEquals(bytes, read)) throw new NotionException("keychain_verification_failed");
            }
            finally { if (read is not null) CryptographicOperations.ZeroMemory(read); }
            if (id is null)
            {
                var found = await _http.SendWithTokenAsync(HttpMethod.Post, "/v1/search", System.Text.Json.JsonDocument.Parse("{\"page_size\":100,\"filter\":{\"value\":\"data_source\",\"property\":\"object\"}}").RootElement.Clone(), bytes, ct);
                id = NotionIds.Canonical(TaskSource(found));
            }
            ct.ThrowIfCancellationRequested();
            return new Change(this, id, account, Read().Generation, token is null);
        }
        catch
        {
            try { await _secrets.DeleteAsync(Service, account, CancellationToken.None); } catch { /* No active pointer was changed. */ }
            _lease.Release();
            throw;
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    // A task database has a title and a Status. One named Tasks wins, otherwise it must be the only one.
    private static string TaskSource(JsonElement found)
    {
        var sources = found.GetProperty("results").EnumerateArray()
            .Where(x => x.GetProperty("object").GetString() == "data_source" && x.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("Status", out _) && p.EnumerateObject().Any(q => q.Value.TryGetProperty("type", out var t) && t.GetString() == "title"))
            .ToArray();
        var named = sources.Where(x => x.TryGetProperty("title", out var t) && string.Equals(NotionBody.Plain(t), "tasks", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 1) return named[0].GetProperty("id").GetString()!;
        if (sources.Length != 1 || found.GetProperty("has_more").GetBoolean()) throw new NotionException("ambiguous_data_source");
        return sources[0].GetProperty("id").GetString()!;
    }
    public async Task<IDisposable> LeaseAsync(CancellationToken ct)
    {
        await _lease.WaitAsync(ct);
        return new Lease(_lease);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }
    public async Task<NotionSettingsView> ClearAsync(CancellationToken ct)
    {
        await _lease.WaitAsync(ct);
        try { _db.Writer.Write("notion.clearSettings", Clear); }
        finally { _lease.Release(); }
        await ReconcileAsync(ct);
        return Read();
    }
    public NotionSettingsView Clear(IWriteScope scope)
    {
        EnqueueOld(scope);
        scope.Execute("UPDATE notion_connection SET data_source_id='',secret_account=NULL,generation=generation+1,state='clearing' WHERE singleton=1");
        Invalidate(scope);
        return Publish(scope);
    }
    private static void EnqueueOld(IWriteScope s) => s.Execute("INSERT OR IGNORE INTO notion_secret_cleanup(account) SELECT secret_account FROM notion_connection WHERE singleton=1 AND secret_account IS NOT NULL");
    private static void Invalidate(IWriteScope s)
    {
        if (s.QueryFirst("SELECT name FROM sqlite_master WHERE type='table' AND name='notion_tasks'", r => r.Text(0)) is not null)
        {
            s.Execute("DELETE FROM notion_tasks");
            s.Execute("DELETE FROM notion_cache_meta");
        }
    }
    private static NotionSettingsView Publish(IWriteScope s)
    {
        var view = s.QueryFirst("SELECT data_source_id,secret_account,generation,state FROM notion_connection WHERE singleton=1", r => new NotionSettingsView(r.Text(0), !r.IsNull(1), r.Long(2), r.Text(3), null))!;
        s.Publish("notion-settings", null, view, NotionJson.Default.NotionSettingsView);
        return view;
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _lease.WaitAsync(ct);
        try
        {
            foreach (var account in _db.Query("SELECT account FROM notion_secret_cleanup ORDER BY account", r => r.Text(0), ct))
            {
                try { await _secrets.DeleteAsync(Service, account, ct); }
                catch (NotionException) { return; }
                _db.Writer.Write("notion.secretCleanup", s => s.Execute("DELETE FROM notion_secret_cleanup WHERE account=$a", new SqlArg("$a", account)));
            }
            _db.Writer.Write("notion.cleanupComplete", s =>
            {
                var changed = s.Execute("UPDATE notion_connection SET state='unconfigured' WHERE state='clearing' AND NOT EXISTS(SELECT 1 FROM notion_secret_cleanup)");
                return changed != 0 ? Publish(s) : null;
            });
        }
        finally { _lease.Release(); }
    }
    public async Task<byte[]> TokenAsync(long generation, CancellationToken ct)
    {
        Check(generation);
        var account = _db.QueryFirst("SELECT secret_account FROM notion_connection WHERE singleton=1", r => r.IsNull(0) ? null : r.Text(0), ct);
        if (account is null) throw new NotionException("missing_token");
        var token = await _secrets.GetAsync(Service, account, ct) ?? throw new NotionException("missing_token");
        try { Check(generation); return token; }
        catch { CryptographicOperations.ZeroMemory(token); throw; }
    }
    public void Check(long generation)
    {
        if (Read().Generation != generation) throw new NotionException("notion_connection_changed");
    }
    public sealed class Change(NotionSettings owner, string source, string account, long generation, bool adoptedBase) : IDisposable
    {
        private bool _disposed;
        public NotionSettingsView Commit(IWriteScope s)
        {
            var current = s.QueryFirst("SELECT generation FROM notion_connection WHERE singleton=1", r => r.Long(0));
            if (current != generation) throw new NotionException("notion_connection_changed");
            EnqueueOld(s);
            if (adoptedBase) s.Execute("INSERT OR IGNORE INTO notion_secret_cleanup(account) VALUES('integration-token')");
            s.Execute("UPDATE notion_connection SET data_source_id=$s,secret_account=$a,generation=generation+1,state='connected' WHERE singleton=1", new SqlArg("$s", source), new SqlArg("$a", account));
            Invalidate(s);
            s.Execute("DELETE FROM notion_secret_cleanup WHERE account=$a", new SqlArg("$a", account));
            return Publish(s);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._lease.Release();
        }
    }
}
