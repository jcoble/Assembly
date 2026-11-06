using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Engine;
using Assembly.Engine;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Claude 2.1.316 JSONL paths, using an owning producer's current native witness.</summary>
public sealed class ClaudeSessionFile : IProviderSessionFile
{
    public string Provider => "claude";

    public string? SqliteHome(string providerHome) => null;

    public NativeFile Select(NativeSessionLaunch launch) =>
        new($"projects/{ProjectKey(launch.Folder)}/{launch.AcpSessionId}.jsonl", launch.AcpSessionId, null);

    public NativeFile? Locate(ProviderFileContext storage, NativeSessionWitness current)
    {
        Validate(storage, current);
        var path = NativeSessionFileInputs.ResolveProviderPath(storage.ProviderHome, current.SelectedRelativePath);
        if (!File.Exists(path)) return null;
        ValidateMetadata(path, current.ResumeId);
        return new NativeFile(current.SelectedRelativePath, current.ResumeId, null);
    }

    public string TargetPath(ProviderFileContext target, string newFolder,
        NativeSessionCapture source, NativeFile file)
    {
        Validate(source.Storage, source.Current);
        if (file.RelativePath != source.Current.SelectedRelativePath ||
            file.NativeId != source.Current.ResumeId || file.RolloutId is not null)
            throw new HandoffRefused("native_witness_mismatch");
        if (target.VendorFamily != Provider || string.IsNullOrWhiteSpace(target.MachineId) ||
            string.IsNullOrWhiteSpace(target.ProviderId) || !AccountContext.IsValid(target.AccountContext) ||
            string.IsNullOrWhiteSpace(target.LaunchBindingId))
            throw new HandoffRefused("native_witness_mismatch");
        // Target roots are supplied by its actual launch context; never open the source account here.
        return NativeSessionFileInputs.ResolveProviderPath(target.ProviderHome,
            $"projects/{ProjectKey(newFolder)}/{file.NativeId}.jsonl");
    }

    public string MapLine(string jsonLine, string oldFolder, string newFolder)
    {
        var oldRoot = Folder(oldFolder);
        var newRoot = Folder(newFolder);
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new HandoffRefused("invalid_native_json");
            string? mapped = null;
            var count = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "cwd") continue;
                if (++count != 1 || property.Value.ValueKind != JsonValueKind.String)
                    throw new HandoffRefused("invalid_native_json");
                var cwd = property.Value.GetString()!;
                if (cwd == oldRoot) mapped = newRoot;
                else if (cwd.StartsWith(oldRoot, StringComparison.Ordinal) &&
                    (Separator(oldRoot[^1]) || cwd.Length > oldRoot.Length && Separator(cwd[oldRoot.Length])))
                {
                    var suffix = cwd[oldRoot.Length..];
                    if (Separator(oldRoot[^1])) suffix = oldRoot[^1] + suffix;
                    mapped = newRoot + (Separator(newRoot[^1]) ? suffix[1..] : suffix);
                }
            }
            if (mapped is null) return jsonLine;
            // Replace only the top-level value token; preserve all other bytes, whitespace and newline suffix.
            var bytes = Encoding.UTF8.GetBytes(jsonLine);
            var reader = new Utf8JsonReader(bytes);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                    !reader.ValueTextEquals("cwd")) continue;
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                var end = (int)reader.BytesConsumed;
                return Encoding.UTF8.GetString(bytes.AsSpan(0, start)) + JsonSerializer.Serialize(mapped, EngineJson.Default.String) +
                    Encoding.UTF8.GetString(bytes.AsSpan(end));
            }
            throw new HandoffRefused("invalid_native_json");
        }
        catch (JsonException) { throw new HandoffRefused("invalid_native_json"); }
    }

    /// <summary>Source-derived from Claude's JS UTF-16/int32 project-key algorithm, not a native capture.</summary>
    public static string ProjectKey(string folder)
    {
        Folder(folder);
        var key = new string(folder.Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            ? c : '-').ToArray());
        if (key.Length <= 200) return key;
        var hash = 0;
        foreach (var unit in folder) hash = unchecked(hash * 31 + unit);
        var value = Math.Abs((long)hash);
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var suffix = "";
        do { suffix = digits[(int)(value % 36)] + suffix; value /= 36; } while (value != 0);
        return key[..200] + "-" + suffix;
    }

    private void Validate(ProviderFileContext storage, NativeSessionWitness current)
    {
        if (current.HistoryMode != "single_file_jsonl" || current.Compression != "none")
            throw new HandoffRefused("unsupported");
        if (storage.VendorFamily != Provider || storage.MachineId != current.MachineId ||
            storage.ProviderId != current.ProviderId || storage.AccountContext != current.AccountContext ||
            storage.LaunchBindingId != current.LaunchBindingId ||
            !AccountContext.IsValid(current.AccountContext) ||
            new[] { current.SessionId, current.MachineId, current.ProviderId,
                current.LaunchBindingId, current.AcpSessionId }.Any(string.IsNullOrWhiteSpace) || current.Epoch < 0 ||
            !Guid.TryParseExact(current.ResumeId, "D", out _) || current.RolloutId is not null)
            throw new HandoffRefused("native_witness_mismatch");
        Folder(current.Folder);
        var parts = current.SelectedRelativePath.Split('/');
        if (parts.Length != 3 || parts[0] != "projects" || parts[1] is "" or "." or ".." ||
            parts[2] != current.ResumeId + ".jsonl" || current.SelectedRelativePath.Contains('\\') ||
            current.SelectedRelativePath.Contains('\0'))
            throw new HandoffRefused("invalid_native_path");
    }

    private static void ValidateMetadata(string path, string nativeId)
    {
        // 2.1.316 J2r/session transcript writer stamps top-level sessionId on conversation records;
        // the resume reader derives identity from the selected user/assistant leaf's sessionId.
        // Other records (e.g. file-history-snapshot) legitimately have no sessionId.
        var found = false;
        try
        {
            foreach (var line in File.ReadLines(path, new UTF8Encoding(false, true)))
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) throw new HandoffRefused("invalid_native_json");
                var types = row.EnumerateObject().Where(property => property.Name == "type").ToArray();
                if (types.Length > 1) throw new HandoffRefused("invalid_native_json");
                var type = types.Length == 1 && types[0].Value.ValueKind == JsonValueKind.String
                    ? types[0].Value.GetString() : null;
                var identities = row.EnumerateObject().Where(property => property.Name == "sessionId").ToArray();
                if (identities.Length > 1) throw new HandoffRefused("invalid_native_json");
                if (type is not ("user" or "assistant")) continue;
                if (identities.Length != 1 || identities[0].Value.ValueKind != JsonValueKind.String ||
                    identities[0].Value.GetString() != nativeId)
                    throw new HandoffRefused("native_witness_mismatch");
                found = true;
            }
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new HandoffRefused("invalid_native_json"); }
        if (!found) throw new HandoffRefused("native_witness_mismatch");
    }

    private static string Folder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Contains('\0') ||
            !(folder[0] == '/' || folder.Length >= 3 && char.IsAsciiLetter(folder[0]) && folder[1] == ':' && Separator(folder[2])))
            throw new HandoffRefused("invalid_native_path");
        var length = folder.Length;
        while (length > 1 && Separator(folder[length - 1]) && !(length == 3 && folder[1] == ':')) length--;
        return folder[..length];
    }

    private static bool Separator(char value) => value is '/' or '\\';
}
