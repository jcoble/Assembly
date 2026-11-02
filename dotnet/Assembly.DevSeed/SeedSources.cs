using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Transcript;
using Microsoft.Data.Sqlite;

namespace Assembly.DevSeed;

public static class SeedSources
{
    public static SeedBundle Read(string manifestPath, CancellationToken cancel = default)
    {
        manifestPath = SeedFiles.Input(manifestPath);
        var manifest = SeedJson.Parse<SeedManifest>(File.ReadAllText(manifestPath));
        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.OriginId) || manifest.Kind is not ("real-recorded" or "synthetic") || manifest.Sources.Count == 0)
            throw new InvalidDataException("Invalid version-1 seed manifest.");
        var inputs = new Dictionary<string, SeedInput>(StringComparer.Ordinal);
        void Track(string path)
        {
            path = SeedFiles.Input(path);
            inputs[path] = new(path, SeedFiles.HashFile(path));
        }
        Track(manifestPath);
        var sessions = new Dictionary<string, SeedSession>(StringComparer.Ordinal);
        var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var dedupe = 0; var remote = 0;
        var limitations = new List<string>();
        foreach (var source in manifest.Sources)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                Track(source.Path);
                if (source.Sha256 is not null && source.Sha256 != inputs[SeedFiles.Input(source.Path)].Sha256) throw new InvalidDataException("Source hash mismatch.");
                if (source.ProviderConfidence is not ("verified" or "uncertain" or "conflicting") || string.IsNullOrWhiteSpace(source.Provider) ||
                    source.ProviderConfidence != "verified" && source.Provider != "unknown")
                    throw new InvalidDataException("Unresolved provider claims require the unknown archival label.");
                if (source.Provenance.Count == 0) throw new InvalidDataException("Explicit source provenance is required.");
                foreach (var file in source.Files) { SeedFiles.CheckAsset(file, false, null); Track(file.SourcePath); }
                foreach (var file in source.Attachments) { SeedFiles.CheckAsset(file, true, source.AttachmentRoot); Track(file.SourcePath); }
                List<SeedSession> read;
                switch (source.Format)
                {
                    case "acp":
                        if (string.IsNullOrEmpty(source.SessionKey)) throw new InvalidDataException("ACP sessionKey is required.");
                        read = [Replay(source)];
                        limitations.Add("ACP recordings retain only captured capabilities; generated surrounding files are synthetic.");
                        break;
                    case "normalized":
                        if (source.RowFormat != 4 || string.IsNullOrEmpty(source.SessionKey)) throw new InvalidDataException("Normalized source requires rowFormat 4 and sessionKey.");
                        var rowJson = File.ReadAllText(source.Path);
                        using (var document = JsonDocument.Parse(rowJson))
                        {
                            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Normalized rows must be an array.");
                            foreach (var value in document.RootElement.EnumerateArray())
                                foreach (var property in new[] { "seq", "runId", "itemId", "kind", "payload", "createdAt" })
                                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out _)) throw new InvalidDataException("Missing required normalized stored-row field.");
                        }
                        var rows = SeedJson.Parse<List<SeedRow>>(rowJson);
                        var details = new List<ToolDetail>();
                        foreach (var path in source.DetailPaths) { Track(path); details.AddRange(SeedJson.Parse<List<ToolDetail>>(File.ReadAllText(path))); }
                        if (source.Path.EndsWith(".rows.json", StringComparison.Ordinal)) limitations.Add("Collapsed rows projection: completeness is limited to explicitly supplied rows/details.");
                        read = [Session(source, source.SessionKey, source.Metadata, rows, details)];
                        break;
                    case "database":
                        using (var db = OpenReadOnly(source.Path))
                        {
                            ValidateSchema(db);
                            using (var count = db.CreateCommand()) { count.CommandText = "SELECT COUNT(*) FROM session_info WHERE machine IS NOT NULL;"; remote += Convert.ToInt32(count.ExecuteScalar()); }
                            read = ReadDatabase(db, source);
                        }
                        break;
                    default: throw new InvalidDataException("Unsupported source format.");
                }
                foreach (var asset in source.Files.Concat(source.Attachments))
                    if (asset.SessionKey is not null && !read.Any(session => session.Key == asset.SessionKey))
                        throw new InvalidDataException("Asset references an unselected or unknown source session.");
                foreach (var readSession in read)
                {
                    var artifactPaths = new[] { manifestPath, source.Path }.Concat(source.DetailPaths).Concat(readSession.Files.Select(file => file.SourcePath)).Concat(readSession.Attachments.Select(file => file.SourcePath));
                    var session = readSession with { Inputs = artifactPaths.Select(path => inputs[SeedFiles.Input(path)]).Distinct().OrderBy(input => input.Path, StringComparer.Ordinal).ToArray() };
                    sourcePaths.TryAdd(session.Key, source.Path);
                    if (sessions.TryGetValue(session.Key, out var existing))
                    {
                        if (SeedJson.Stringify(existing with { Rows = [], Details = [], Provenance = [], Files = [], Attachments = [], Inputs = [] }) != SeedJson.Stringify(session with { Rows = [], Details = [], Provenance = [], Files = [], Attachments = [], Inputs = [] }))
                            throw new InvalidDataException("Conflicting overlapping session metadata.");
                        var merged = existing.Rows.ToDictionary(r => r.Seq);
                        foreach (var row in session.Rows)
                        {
                            if (merged.TryGetValue(row.Seq, out var prior))
                            {
                                if (SeedJson.Stringify(prior) != SeedJson.Stringify(row)) throw new InvalidDataException("Conflicting overlapping session/seq.");
                                dedupe++;
                            }
                            else merged.Add(row.Seq, row);
                        }
                        var detailMap = existing.Details.ToDictionary(d => d.ItemId);
                        foreach (var detail in session.Details)
                        {
                            if (detailMap.TryGetValue(detail.ItemId, out var prior) && prior != detail) throw new InvalidDataException("Conflicting overlapping detail.");
                            detailMap[detail.ItemId] = detail;
                        }
                        sessions[session.Key] = existing with
                        {
                            Rows = merged.Values.OrderBy(r => r.Seq).ToArray(),
                            Details = detailMap.Values.OrderBy(d => d.ItemId, StringComparer.Ordinal).ToArray(),
                            Provenance = existing.Provenance.Concat(session.Provenance).Distinct().ToArray(),
                            Files = existing.Files.Concat(session.Files).Distinct().ToArray(),
                            Attachments = existing.Attachments.Concat(session.Attachments).Distinct().ToArray(),
                            Inputs = (existing.Inputs ?? []).Concat(session.Inputs ?? []).Distinct().OrderBy(input => input.Path, StringComparer.Ordinal).ToArray()
                        };
                    }
                    else sessions.Add(session.Key, session);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new InvalidDataException($"Source '{source.Path}': {error.Message}", error); }
        }
        foreach (var session in sessions.Values)
        {
            try
            {
                ValidateSession(session);
                var seen = new HashSet<string>(StringComparer.Ordinal) { session.Key };
                var parent = session.Metadata.ParentSessionKey;
                while (parent is not null)
                {
                    if (!seen.Add(parent) || !sessions.TryGetValue(parent, out var ancestor)) throw new InvalidDataException("Parent closure is missing or cyclic.");
                    parent = ancestor.Metadata.ParentSessionKey;
                }
                foreach (var row in session.Rows.Where(r => r.Kind == "subagent"))
                    if (!sessions.ContainsKey(row.Payload.GetProperty("sessionId").GetString()!)) throw new InvalidDataException("Subagent closure is missing.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new InvalidDataException($"Source '{sourcePaths[session.Key]}': {error.Message}", error); }
        }
        return new(manifest, sessions.Values.OrderBy(s => s.Key, StringComparer.Ordinal).ToArray(), inputs.Values.OrderBy(i => i.Path, StringComparer.Ordinal).ToArray(), dedupe, remote, limitations.Distinct().ToArray());
    }
    private static SeedSession Session(SeedSource source, string key, SeedMetadata metadata, IReadOnlyList<SeedRow> rows, IReadOnlyList<ToolDetail> details) =>
        new(key, source.Provider, source.ProviderConfidence, source.OriginalHost, metadata, source.Provenance,
            rows.OrderBy(r => r.Seq).ToArray(), details.OrderBy(d => d.ItemId, StringComparer.Ordinal).ToArray(),
            source.Files.Where(a => a.SessionKey is null || a.SessionKey == key).ToArray(), source.Attachments.Where(a => a.SessionKey is null || a.SessionKey == key).ToArray());
    private static SeedSession Replay(SeedSource source)
    {
        var converter = new RowConverter(); var rows = new List<SeedRow>(); var details = new Dictionary<string, ToolDetail>(); long seq = 1;
        var events = TranscriptReplay.Events(source.Path);
        if (events.Count == 0) throw new InvalidDataException("ACP capture contains no replayable events.");
        foreach (var evt in events)
        {
            var batch = converter.Apply(evt);
            foreach (var row in batch.Rows)
            {
                rows.Add(new(seq++, row.RunId, row.ItemId, row.Kind, row.Payload, row.CreatedAt));
                if (row.Kind == "message") rows.RemoveAll(r => r.ItemId == row.ItemId && r.Kind == "delta");
            }
            foreach (var detail in batch.Details)
            {
                details.TryGetValue(detail.ItemId, out var old);
                details[detail.ItemId] = new(detail.ItemId, detail.Input ?? old?.Input, detail.Output ?? old?.Output, detail.Diff ?? old?.Diff, detail.Path ?? old?.Path);
            }
        }
        var metadata = source.Metadata with { CreatedAt = rows.Min(r => r.CreatedAt), UpdatedAt = rows.Max(r => r.CreatedAt) };
        return Session(source, source.SessionKey!, metadata, rows, details.Values.ToArray());
    }
    public static SqliteConnection OpenReadOnly(string path)
    {
        SeedFiles.Input(path);
        if (File.Exists(path + "-wal") || File.Exists(path + "-shm")) throw new InvalidDataException("A closed compatible safe backup without WAL/SHM is required.");
        // A read-only connection to a WAL-mode file can still create WAL/SHM sidecars. These explicitly
        // closed backups are immutable inputs, so SQLite must neither lock nor create sidecars beside them.
        var uri = new Uri(path).AbsoluteUri + "?immutable=1";
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    public static void ValidateSchema(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt64(command.ExecuteScalar()) != 4) throw new InvalidDataException("Only current SQLite format 4 is supported; initialize isolated profiles through the normal launcher.");
        foreach (var (table, columns) in new[] {
            ("sessions", "session_id,next_seq"), ("rows", "session_id,seq,run_id,item_id,kind,payload,created_at"),
            ("tool_details", "session_id,item_id,input,output,diff,path"),
            ("session_info", "session_id,agent,repo_url,title,acp_session_id,created_at,updated_at,machine,session_cost,parent_session_id,helper,read_seq,completed_at,settled_at,title_source") })
        {
            command.CommandText = $"PRAGMA table_info({table});";
            var actual = new HashSet<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) actual.Add(reader.GetString(1));
            if (columns.Split(',').Any(c => !actual.Contains(c))) throw new InvalidDataException("Missing current schema columns; initialize isolated profile through normal launcher.");
        }
    }
    private static List<SeedSession> ReadDatabase(SqliteConnection db, SeedSource source)
    {
        var result = new List<SeedSession>();
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$keys", source.SessionKeys is null ? DBNull.Value : SeedJson.Stringify(source.SessionKeys));
        command.CommandText = """
            WITH RECURSIVE selected(id) AS (
                SELECT session_id FROM session_info WHERE $keys IS NULL OR session_id IN (SELECT value FROM json_each($keys))
                UNION
                SELECT s.parent_session_id FROM session_info s JOIN selected p ON s.session_id=p.id WHERE s.parent_session_id IS NOT NULL)
            SELECT session_id,title,parent_session_id,created_at,updated_at,session_cost,read_seq,repo_url,acp_session_id,agent
            FROM session_info WHERE machine IS NULL AND session_id IN (SELECT id FROM selected) ORDER BY session_id;
            """;
        var metadata = new Dictionary<string, SeedMetadata>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) metadata.Add(reader.GetString(0), new(Text(reader, 1), Text(reader, 2), reader.GetInt64(3), reader.GetInt64(4), Text(reader, 5), reader.GetInt64(6), Text(reader, 7), Text(reader, 8), Text(reader, 9), ["manifest:" + source.Provider + ":" + source.ProviderConfidence, "stored:" + reader.GetString(9)]));
        var selected = source.SessionKeys is null ? metadata.Keys.ToHashSet() : source.SessionKeys.ToHashSet();
        foreach (var key in selected.ToArray())
        {
            var seen = new HashSet<string>(); var at = key;
            while (at is not null)
            {
                if (!seen.Add(at) || !metadata.TryGetValue(at, out var entry)) throw new InvalidDataException("Selected local parent closure is missing or cyclic.");
                selected.Add(at); at = entry.ParentSessionKey;
            }
        }
        foreach (var key in selected.Order(StringComparer.Ordinal))
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", key);
            command.CommandText = "SELECT seq,run_id,item_id,kind,payload,created_at FROM rows WHERE session_id=$id ORDER BY seq;";
            var rows = new List<SeedRow>(); using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add(new(reader.GetInt64(0), Text(reader, 1), Text(reader, 2), reader.GetString(3), JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(), reader.GetInt64(5)));
            command.CommandText = "SELECT item_id,input,output,diff,path FROM tool_details WHERE session_id=$id ORDER BY item_id;";
            var details = new List<ToolDetail>(); using (var reader = command.ExecuteReader()) while (reader.Read()) details.Add(new(reader.GetString(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4)));
            var attribution = metadata[key].OriginalProvider == source.Provider ? source : source with
            {
                Provider = "unknown",
                ProviderConfidence = source.Provider == "unknown" ? source.ProviderConfidence : "conflicting"
            };
            result.Add(Session(attribution, key, metadata[key], rows, details));
        }
        return result;
    }
    private static string? Text(SqliteDataReader reader, int at) => reader.IsDBNull(at) ? null : reader.GetString(at);
    private static void ValidateSession(SeedSession session)
    {
        if (session.Metadata.SessionCost is not null && !decimal.TryParse(session.Metadata.SessionCost, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("Invalid recorded session cost.");
        if (string.IsNullOrEmpty(session.Key) || session.Rows.Any(r => r.Seq < 1 || r.Seq == long.MaxValue) || session.Rows.Select(r => r.Seq).Distinct().Count() != session.Rows.Count)
            throw new InvalidDataException("Invalid session key or row sequence.");
        if (session.Details.Select(d => d.ItemId).Distinct().Count() != session.Details.Count) throw new InvalidDataException("Duplicate tool detail key.");
        if (session.Details.Any(d => string.IsNullOrEmpty(d.ItemId) || !session.Rows.Any(r => r.Kind == "tool" && r.ItemId == d.ItemId)))
            throw new InvalidDataException("Orphaned tool detail key.");
        foreach (var row in session.Rows)
        {
            var payload = row.Payload;
            if (payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid row payload.");
            // Deserialize through the current typed contract; never accept unknown arbitrary row kinds.
            object? typed = row.Kind switch
            {
                "turn" => payload.Deserialize<TurnPayload>(SeedJson.Options),
                "user" => payload.Deserialize<UserPayload>(SeedJson.Options),
                "delta" => payload.Deserialize<DeltaPayload>(SeedJson.Options),
                "message" => payload.Deserialize<MessagePayload>(SeedJson.Options),
                "thinking" => payload.Deserialize<ThinkingPayload>(SeedJson.Options),
                "tool" => payload.Deserialize<ToolPayload>(SeedJson.Options),
                "plan" => payload.Deserialize<PlanPayload>(SeedJson.Options),
                "stats" => payload.Deserialize<StatsPayload>(SeedJson.Options),
                "error" => payload.Deserialize<ErrorPayload>(SeedJson.Options),
                "ask" => payload.Deserialize<AskPayload>(SeedJson.Options),
                "task" => payload.Deserialize<TaskPayload>(SeedJson.Options),
                "context" => payload.Deserialize<ContextPayload>(SeedJson.Options),
                "subagent" => payload.Deserialize<SubagentPayload>(SeedJson.Options),
                _ => throw new InvalidDataException("Unknown row kind.")
            };
            if (typed is null) throw new InvalidDataException("Null typed row payload.");
            var valid = typed switch
            {
                TurnPayload t => t.State is "started" or "finished" or "failed" or "cancelled",
                UserPayload u => u.Text is not null,
                DeltaPayload d => d.Text is not null && d.Blocks is not null && d.From >= 0,
                MessagePayload m => m.Blocks is not null,
                ThinkingPayload t => t.Text is not null,
                ToolPayload t => t.Name is not null && t.State is "started" or "completed" or "failed" &&
                    (t.Files is null || t.Files.All(f => !string.IsNullOrEmpty(f.Path))),
                PlanPayload p => p.Items is not null && p.Items.All(i => i.Text is not null && i.Status is "pending" or "in_progress" or "completed"),
                StatsPayload t => !string.IsNullOrEmpty(t.RunId) && t.Status is "completed" or "failed" or "aborted",
                ErrorPayload e => e.Message is not null,
                AskPayload a => a.Kind is "permission" or "question" && a.State is "open" or "answered" or "cancelled",
                TaskPayload t => t.Name is not null && t.State is "running" or "completed" or "failed" or "stopped",
                ContextPayload c => c.Used >= 0 && c.Size > 0,
                SubagentPayload a => !string.IsNullOrEmpty(a.SessionId) && a.Title is not null && a.State is "running" or "requires_action" or "idle",
                _ => false
            };
            if (!valid) throw new InvalidDataException("Invalid required fields in typed row payload.");
            // Default-valued constructor arguments cannot distinguish an omitted boolean from false.
            if (row.Kind == "tool" && !payload.TryGetProperty("hasDetail", out _) ||
                row.Kind == "thinking" && !payload.TryGetProperty("done", out _))
                throw new InvalidDataException("Missing required typed row payload field.");
            if (typed is ToolPayload { HasDetail: true } && !session.Details.Any(d => d.ItemId == row.ItemId && (d.Input is not null || d.Output is not null || d.Diff is not null)))
                throw new InvalidDataException("Tool promises detail without complete real detail sidecar.");
        }
        foreach (var latest in session.Rows.Where(r => r.Kind is "turn" or "task" or "subagent" or "ask" or "tool" or "thinking" or "delta")
            .GroupBy(r => (r.Kind, Key: r.Kind == "turn" ? r.RunId : r.ItemId)).Select(g => g.MaxBy(r => r.Seq)!))
        {
            var state = latest.Payload.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (state is "started" or "running" or "requires_action" or "open" ||
                latest.Kind == "thinking" && !latest.Payload.GetProperty("done").GetBoolean() || latest.Kind == "delta")
                throw new InvalidDataException("Unfinished capture or active task/turn/ask is refused.");
        }
    }
}
