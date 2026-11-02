using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public static partial class MessageTransitions
{
    internal static string NeedResolutionText(string needId, string state, JsonElement? answer) => state switch
    {
        "answered" => $"Jesse answered your question {needId}: {(answer!.Value.ValueKind == JsonValueKind.String ? answer.Value.GetString() : JsonSerializer.Serialize(answer.Value, EngineJson.Default.JsonElement))}",
        "dismissed" => $"Jesse dismissed your question {needId}.",
        _ => $"Your question {needId} closed without an answer ({state})."
    };

    internal static IReadOnlyList<EventDraft> NeedOpened(IWriteScope scope, CommandContext context, string needId,
        OpenNeed question, string parent, string optionsJson, IList<EffectDraft> effects, MessageExpiryPolicy? policy)
    {
        var title = scope.QueryFirst("""
            SELECT CAST(child.title AS BLOB) FROM engine_session child JOIN engine_needs_me need ON need.session_id=child.id
            WHERE need.id=$need AND child.id=$child AND child.parent_id=$parent AND need.addressee_session_id=$parent
            """, r => new UTF8Encoding(false, true).GetString(r.Blob(0)), new SqlArg("$need", needId),
            new("$child", question.SessionId), new("$parent", parent))
            ?? throw new InvalidOperationException("Need notification identity changed.");
        var text = $"Child session {title} ({question.SessionId}) asks: {question.Text}. Options: {optionsJson}. Need id: {needId}.";
        if (question.ProviderQuestion is { } card) text += " Provider question: " + card.GetRawText();
        var body = new UTF8Encoding(false, true).GetBytes(text);
        var id = "need/" + needId;
        var existing = scope.QueryFirst("""
            SELECT from_session_id,to_session_id,sender_kind,CAST(text AS BLOB),CAST(sender_label AS BLOB),mode,delivery
            FROM engine_message WHERE id=$id
            """, r => new
        {
            From = r.IsNull(0) ? null : r.Text(0),
            To = r.Text(1),
            Kind = r.Text(2),
            Body = r.Blob(3),
            Label = r.Blob(4),
            Mode = r.Text(5),
            Delivery = r.Text(6)
        }, new SqlArg("$id", id));
        if (existing is not null)
        {
            if (existing.From != question.SessionId || existing.To != parent || existing.Kind != "system" ||
                !existing.Body.AsSpan().SequenceEqual(body) || !existing.Label.AsSpan().SequenceEqual("Assembly"u8) ||
                existing.Mode != "prompt" || existing.Delivery != "steer")
                throw new InvalidOperationException("Need notification identity conflict.");
            return [];
        }
        var recipient = FindRecipient(scope, parent) ?? throw new InvalidOperationException("Need addressee disappeared.");
        var ended = recipient.State == "ended";
        var message = new Message(id, 1, question.SessionId, parent, text, "prompt", ended ? "failed" : "queued");
        Insert(scope, context, message, "system", ended ? "starter_ended" : null, policy, body);
        List<EventDraft> events = [Changed(context, message)];
        if (!ended && recipient.State != "foreign") ReleaseNext(scope, context, parent, effects, events);
        return events;
    }
}
