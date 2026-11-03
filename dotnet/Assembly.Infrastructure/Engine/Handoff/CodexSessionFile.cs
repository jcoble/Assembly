using System.Globalization;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Agents;
using Assembly.Engine;
using Microsoft.Data.Sqlite;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Source-derived Codex rust-v0.166.0 JSONL layout. This is not a native witness producer.</summary>
public sealed class CodexSessionFile : IProviderSessionFile
{
    public string Provider => "codex";

    public NativeFile? Locate(ProviderFileContext storage, NativeSessionWitness current)
    {
        ValidateBinding(storage, current);
        RequireFormat(current.HistoryMode, current.Compression);
        var selected = ParsePath(current.SelectedRelativePath);
        Match(selected, current);
        var chosen = NativeSessionFileInputs.ResolveProviderPath(storage.ProviderHome, current.SelectedRelativePath);
        if (string.IsNullOrWhiteSpace(storage.CodexSqliteHome)) throw Refused("unavailable");
        var state = NativeSessionFileInputs.ResolveProviderPath(storage.CodexSqliteHome, "state_5.sqlite");
        var pointer = ReadPointer(state, current.ResumeId);
        if (pointer is not null)
        {
            if (pointer.Archived) throw Refused("unavailable");
            // history_mode is opaque native metadata. The accepted P2 producer must verify the canonical port format;
            // never compare a raw SQLite value with the port's single_file_jsonl label.
            if (!Path.IsPathFullyQualified(pointer.Path)) throw Refused("bad_args");
            var relative = Path.GetRelativePath(storage.ProviderHome, pointer.Path);
            Match(ParsePath(relative), current);
            var actual = NativeSessionFileInputs.ResolveProviderPath(storage.ProviderHome, relative);
            if (actual != chosen) throw Refused("bad_args");
            ValidateMetadata(actual, current);
            return selected;
        }
        // Only legacy stores without a row may fall back; never bypass a bad current pointer.
        NativeFile[] candidates;
        try { candidates = Candidates(storage.ProviderHome, current).Take(2).ToArray(); }
        catch (IOException) { throw Refused("unavailable"); }
        catch (UnauthorizedAccessException) { throw Refused("unavailable"); }
        if (candidates.Length == 0) return null;
        if (candidates.Length != 1) throw Refused("ambiguous_native_session");
        if (candidates[0] != selected) throw Refused("bad_args");
        return selected;
    }

    public string TargetPath(ProviderFileContext target, string newFolder, NativeSessionCapture source, NativeFile file)
    {
        ValidateBinding(source.Storage, source.Current);
        RequireFormat(source.Current.HistoryMode, source.Current.Compression);
        Match(file, source.Current);
        if (file != ParsePath(file.RelativePath) || target is null || !AccountContext.IsValid(target.AccountContext) || target.VendorFamily != Provider ||
            string.IsNullOrWhiteSpace(target.MachineId) || string.IsNullOrWhiteSpace(target.ProviderId) ||
            string.IsNullOrWhiteSpace(target.LaunchBindingId)) throw Refused("bad_args");
        _ = MapFolder(newFolder, newFolder, newFolder);
        // Resolve only the target account. Source roots are immutable identity, never opened here.
        var path = NativeSessionFileInputs.ResolveProviderPath(target.ProviderHome, file.RelativePath);
        if (string.IsNullOrWhiteSpace(target.CodexSqliteHome)) throw Refused("unavailable");
        var state = NativeSessionFileInputs.ResolveProviderPath(target.CodexSqliteHome, "state_5.sqlite");
        var existing = ReadPointer(state, file.NativeId);
        if (existing is not null)
        {
            if (existing.Archived) throw Refused("unavailable");
            if (!Path.IsPathFullyQualified(existing.Path) ||
                NativeSessionFileInputs.ResolveProviderPath(target.ProviderHome, Path.GetRelativePath(target.ProviderHome, existing.Path)) != path)
                throw Refused("bad_args");
        }
        // Same-path final body conflicts are checked by the existing mapped-hash transfer before adoption.
        return path;
    }

    private void ValidateBinding(ProviderFileContext storage, NativeSessionWitness current)
    {
        if (storage is null || current is null || storage.VendorFamily != Provider ||
            string.IsNullOrWhiteSpace(storage.ProviderHome) || !Path.IsPathFullyQualified(storage.ProviderHome) ||
            !AccountContext.IsValid(storage.AccountContext) || !AccountContext.IsValid(current.AccountContext) ||
            string.IsNullOrWhiteSpace(current.SelectedRelativePath) || string.IsNullOrWhiteSpace(storage.MachineId) ||
            string.IsNullOrWhiteSpace(storage.ProviderId) || string.IsNullOrWhiteSpace(storage.LaunchBindingId) ||
            string.IsNullOrWhiteSpace(current.SessionId) || current.Epoch < 0 ||
            storage.MachineId != current.MachineId || storage.ProviderId != current.ProviderId ||
            storage.AccountContext != current.AccountContext || storage.LaunchBindingId != current.LaunchBindingId ||
            !Guid.TryParseExact(current.ResumeId, "D", out _) || current.AcpSessionId != current.ResumeId)
            throw Refused("bad_args");
        _ = MapFolder(current.Folder, current.Folder, current.Folder);
    }

    private static void Match(NativeFile file, NativeSessionWitness current)
    {
        if (file.NativeId != current.ResumeId || file.RolloutId != current.RolloutId ||
            file.RelativePath != current.SelectedRelativePath) throw Refused("bad_args");
    }

    private static NativeFile ParsePath(string relative)
    {
        if (relative.StartsWith("archived_sessions/", StringComparison.Ordinal)) throw Refused("unavailable");
        if (relative.EndsWith(".jsonl.zst", StringComparison.Ordinal)) throw Refused("unsupported");
        var parts = relative.Split('/');
        if (parts.Length != 5 || parts[0] != "sessions" || parts[1].Length != 4 || parts[2].Length != 2 || parts[3].Length != 2)
            throw Refused("bad_args");
        var name = parts[4];
        if (!name.StartsWith("rollout-", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal) || name.Length < 70)
            throw Refused("bad_args");
        var stamp = name.Substring(8, 19);
        if (name[27] != '-' || !DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) || stamp[..10] != string.Join("-", parts[1..4])) throw Refused("bad_args");
        var ids = name[28..^6].Split('_');
        if (ids.Length is not (1 or 2) || !Guid.TryParseExact(ids[0], "D", out _) ||
            ids.Length == 2 && !Guid.TryParseExact(ids[1], "D", out _)) throw Refused("bad_args");
        return new(relative, ids[0], ids.Length == 2 ? ids[1] : null);
    }

    private static Pointer? ReadPointer(string path, string id)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rollout_path,history_mode,archived_at FROM threads WHERE id=$nativeId";
            command.Parameters.AddWithValue("$nativeId", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (reader.IsDBNull(0)) throw Refused("unavailable");
            var result = new Pointer(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), !reader.IsDBNull(2));
            if (reader.Read()) throw Refused("bad_args");
            return result;
        }
        catch (SqliteException) { throw Refused("unavailable"); }
    }

    private static IEnumerable<NativeFile> Candidates(string home, NativeSessionWitness current)
    {
        var root = NativeSessionFileInputs.ResolveProviderPath(home, "sessions");
        if (!Directory.Exists(root)) yield break;
        foreach (var year in Directory.EnumerateDirectories(root))
        {
            var y = Path.GetFileName(year);
            if (y.Length != 4 || !y.All(char.IsAsciiDigit)) continue;
            var checkedYear = NativeSessionFileInputs.ResolveProviderPath(home, "sessions/" + y);
            foreach (var month in Directory.EnumerateDirectories(checkedYear))
            {
                var m = Path.GetFileName(month);
                if (m.Length != 2 || !m.All(char.IsAsciiDigit)) continue;
                var checkedMonth = NativeSessionFileInputs.ResolveProviderPath(home, $"sessions/{y}/{m}");
                foreach (var day in Directory.EnumerateDirectories(checkedMonth))
                {
                    var d = Path.GetFileName(day);
                    if (d.Length != 2 || !d.All(char.IsAsciiDigit)) continue;
                    var checkedDay = NativeSessionFileInputs.ResolveProviderPath(home, $"sessions/{y}/{m}/{d}");
                    foreach (var path in Directory.EnumerateFiles(checkedDay, "rollout-*"))
                    {
                        var name = Path.GetFileName(path);
                        if (!name.Contains(current.ResumeId, StringComparison.Ordinal)) continue;
                        var relative = $"sessions/{y}/{m}/{d}/{name}";
                        var file = ParsePath(relative);
                        if (file.NativeId != current.ResumeId) continue;
                        ValidateMetadata(NativeSessionFileInputs.ResolveProviderPath(home, relative), current);
                        yield return file;
                    }
                }
            }
        }
    }

    private static void ValidateMetadata(string path, NativeSessionWitness current)
    {
        if (!File.Exists(path)) throw Refused("unavailable");
        try
        {
            using var lines = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var first = lines.ReadLine() ?? throw Refused("bad_args");
            using var document = JsonDocument.Parse(first);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta" ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != current.ResumeId ||
                !payload.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String || cwd.GetString() != current.Folder)
                throw Refused("bad_args");
            if (root.EnumerateObject().Count(property => property.Name == "type") != 1 ||
                root.EnumerateObject().Count(property => property.Name == "payload") != 1 ||
                payload.EnumerateObject().Count(property => property.Name == "id") != 1 ||
                payload.EnumerateObject().Count(property => property.Name == "cwd") != 1) throw Refused("bad_args");
        }
        catch (JsonException) { throw Refused("bad_args"); }
        catch (DecoderFallbackException) { throw Refused("bad_args"); }
        catch (IOException) { throw Refused("unavailable"); }
        catch (UnauthorizedAccessException) { throw Refused("unavailable"); }
    }

    // RawHistoryMode is retained as opaque metadata, never treated as a canonical format witness.
    private sealed record Pointer(string Path, string? RawHistoryMode, bool Archived);

    public string MapLine(string jsonLine, string oldFolder, string newFolder)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(jsonLine);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Refused("bad_args");
            var types = root.EnumerateObject().Where(property => property.Name == "type").ToArray();
            if (types.Length != 1 || types[0].Value.ValueKind != JsonValueKind.String) throw Refused("bad_args");
            if (types[0].Value.GetString() is not ("session_meta" or "turn_context")) return jsonLine;
            var payloads = root.EnumerateObject().Where(property => property.Name == "payload").ToArray();
            if (payloads.Length != 1 || payloads[0].Value.ValueKind != JsonValueKind.Object) throw Refused("bad_args");
            if (types[0].Value.GetString() == "session_meta")
            {
                var ids = payloads[0].Value.EnumerateObject().Where(property => property.Name == "id").ToArray();
                if (ids.Length != 1 || ids[0].Value.ValueKind != JsonValueKind.String ||
                    !Guid.TryParseExact(ids[0].Value.GetString(), "D", out _)) throw Refused("bad_args");
            }
            var paths = payloads[0].Value.EnumerateObject().Where(property => property.Name == "cwd").ToArray();
            if (paths.Length != 1 || paths[0].Value.ValueKind != JsonValueKind.String) throw Refused("bad_args");
            var cwd = paths[0].Value.GetString()!;
            var mapped = MapFolder(cwd, oldFolder, newFolder);
            if (mapped == cwd) return jsonLine;
            // Replace only the exact payload.cwd string token. Other JSON bytes, including prompt/tool text, survive.
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var property = reader.GetString();
                reader.Read();
                if (property != "payload") { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var field = reader.GetString();
                    reader.Read();
                    if (field != "cwd") { reader.Skip(); continue; }
                    var start = checked((int)reader.TokenStartIndex);
                    var end = checked((int)reader.BytesConsumed);
                    return Encoding.UTF8.GetString(bytes.AsSpan(0, start)) + JsonSerializer.Serialize(mapped, EngineJson.Default.String)
                        + Encoding.UTF8.GetString(bytes.AsSpan(end));
                }
                break;
            }
            throw Refused("bad_args");
        }
        catch (JsonException) { throw Refused("bad_args"); }
    }

    private static string MapFolder(string cwd, string oldFolder, string newFolder)
    {
        static string Root(string value)
        {
            if (string.IsNullOrEmpty(value) || !(value.StartsWith('/') || value.Length >= 3 && char.IsAsciiLetter(value[0])
                && value[1] == ':' && value[2] is '/' or '\\')) throw Refused("bad_args");
            var minimum = value.StartsWith('/') ? 1 : 3;
            while (value.Length > minimum && value[^1] is '/' or '\\') value = value[..^1];
            return value;
        }
        var oldRoot = Root(oldFolder);
        var newRoot = Root(newFolder);
        if (cwd == oldRoot) return newRoot;
        var prefix = oldRoot[^1] is '/' or '\\' ? oldRoot : oldRoot + (oldRoot.Contains('\\') ? "\\" : "/");
        if (!cwd.StartsWith(prefix, StringComparison.Ordinal)) return cwd;
        var suffix = cwd[prefix.Length..];
        var separator = newRoot.Contains('\\') ? "\\" : "/";
        return newRoot + (newRoot[^1] is '/' or '\\' ? "" : separator) + suffix;
    }

    private static void RequireFormat(string historyMode, string compression)
    {
        // Canonical PORT labels, emitted only by a producer that verified a self-contained plain JSONL history.
        // They are not Codex raw SQLite/CLI enum values and do not claim compressed or paginated support.
        if (historyMode != "single_file_jsonl" || compression != "none") throw Refused("unsupported");
    }

    private static HandoffRefused Refused(string reason) => new(reason);
}
