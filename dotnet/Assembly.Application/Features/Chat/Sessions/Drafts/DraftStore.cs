using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Chat.Sessions.Drafts;

/// <summary>A session's unsent composer draft: its text and the images already saved for it.</summary>
public sealed record SessionDraft(string Text, IReadOnlyList<DraftAttachment> Attachments)
{
    public static readonly SessionDraft Empty = new("", []);
    [JsonIgnore] public bool IsEmpty => Text.Length == 0 && Attachments.Count == 0;
}

/// <summary>An image the draft holds, saved at <paramref name="Path"/> where the session's agent runs.</summary>
public sealed record DraftAttachment(string Name, long Size, string Path);

/// <summary>
/// The unsent draft lives with the session on this machine, as the old app's drafts table did, so it outlasts a
/// restart. The composer keeps the text it is editing and saves every change here; a send clears it with its accept.
/// </summary>
public static class DraftStore
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS session_draft(session_id TEXT PRIMARY KEY, text TEXT NOT NULL,
          attachments TEXT NOT NULL, updated_at INTEGER NOT NULL);
        """;

    /// <summary>Saves <paramref name="draft"/> as the session's draft; an empty one leaves none.</summary>
    public static void Set(IWriteScope scope, string sessionId, SessionDraft draft, long at)
    {
        if (draft.IsEmpty)
        {
            Clear(scope, sessionId);
            return;
        }
        scope.Execute("""
            INSERT INTO session_draft(session_id, text, attachments, updated_at) VALUES($id, $text, $attachments, $at)
            ON CONFLICT(session_id) DO UPDATE SET text=excluded.text, attachments=excluded.attachments, updated_at=excluded.updated_at
            """, new SqlArg("$id", sessionId), new("$text", draft.Text),
            new("$attachments", JsonSerializer.Serialize(draft.Attachments, DraftsJson.Default.IReadOnlyListDraftAttachment)),
            new("$at", at));
    }

    public static void Clear(IWriteScope scope, string sessionId) =>
        scope.Execute("DELETE FROM session_draft WHERE session_id=$id", new SqlArg("$id", sessionId));

    /// <summary>The session's saved draft, empty when it has none.</summary>
    public static SessionDraft Get(IReadScope scope, string sessionId) =>
        scope.QueryFirst("SELECT text, attachments FROM session_draft WHERE session_id=$id",
            row => new SessionDraft(row.Text(0),
                JsonSerializer.Deserialize(row.Text(1), DraftsJson.Default.IReadOnlyListDraftAttachment) ?? []),
            new SqlArg("$id", sessionId)) ?? SessionDraft.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DraftsFeature.SaveDraftArgs))]
[JsonSerializable(typeof(DraftsFeature.Saved))]
[JsonSerializable(typeof(IReadOnlyList<DraftAttachment>))]
internal sealed partial class DraftsJson : JsonSerializerContext;
