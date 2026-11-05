using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Hosting;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Assembly.Infrastructure.Phone;

/// <summary>The retained mobile inbox, using the same checked owner answers as the desktop.</summary>
public static class PhoneNeedsRoutes
{
    public static void Map(IEndpointRouteBuilder routes, Func<HttpContext, PhonePeer?> authenticate,
        EngineRuntime runtime, NeedAnswers answers, Action<IWriteScope, PhonePeer> ensureCurrentPeer)
    {
        routes.MapGet("/phone/v1/inbox", async context =>
        {
            var peer = authenticate(context);
            if (peer is null || string.IsNullOrWhiteSpace(peer.DeviceId))
            { await JsonReply.ErrorAsync(context, 401, "Pair this phone again"); return; }
            var items = ((IEngineRuntime)runtime).Read(scope => Read(scope, runtime.Machine), context.RequestAborted);
            await JsonReply.WriteAsync(context, 200, json => WriteInbox(json, items));
        });
        routes.MapPost("/phone/v1/inbox/{id}/answer", async context =>
        {
            var peer = authenticate(context);
            if (peer is null || string.IsNullOrWhiteSpace(peer.DeviceId))
            { await JsonReply.ErrorAsync(context, 401, "Pair this phone again"); return; }
            var body = await JsonReply.ReadObjectAsync(context);
            if (body is null || !TryAnswer(body.Value, out var option, out var version))
            { await JsonReply.ErrorAsync(context, 400, "invalid answer"); return; }
            // Routing leaves %2F encoded, and a gate's id has slashes in it.
            var id = Uri.UnescapeDataString(context.Request.RouteValues["id"] as string ?? "");
            var item = ((IEngineRuntime)runtime).Read(scope => Read(scope, runtime.Machine, id).SingleOrDefault(), context.RequestAborted);
            if (item is null || item.Version != version)
            { await StaleAsync(context, runtime, id); return; }
            var offered = item.Options.FirstOrDefault(candidate => candidate.Id == option);
            if (offered is null)
            { await JsonReply.ErrorAsync(context, 400, "That option is not offered for this item"); return; }
            var text = JsonReply.String(body.Value, "text");
            if (offered.NeedsText && string.IsNullOrWhiteSpace(text))
            { await JsonReply.ErrorAsync(context, 400, "Say what should change"); return; }
            var answer = AnswerFor(item, option, offered.NeedsText ? text : null);
            var prepared = await answers.PrepareOwnerAsync(new(Guid.NewGuid(), item.Id, version, item.Epoch,
                new ResolveNeed("answered", answer)), context.RequestAborted, new("phone", peer.DeviceId, null));
            try
            {
                var result = ((IEngineRuntime)runtime).Write(scope =>
                {
                    ensureCurrentPeer(scope, peer);
                    context.RequestAborted.ThrowIfCancellationRequested();
                    return prepared(scope);
                });
                if (result.Applied) { context.Response.StatusCode = 204; return; }
                if (result.Error is "already_resolved" or "version_conflict" or "stale_epoch" or "stale_attempt")
                    await StaleAsync(context, runtime, id);
                else await JsonReply.ErrorAsync(context, 400, result.Error ?? "answer refused");
            }
            catch (UnauthorizedAccessException)
            {
                await JsonReply.ErrorAsync(context, 401, "Pair this phone again");
            }
        });
    }

    /// <summary>An answer that came too late. A question closed because its work is being redone was not answered by anyone, so it says so.</summary>
    private static Task StaleAsync(HttpContext context, EngineRuntime runtime, string? id)
    {
        var state = ((IEngineRuntime)runtime).Read(scope => scope.QueryFirst("SELECT state FROM engine_needs_me WHERE id=$id AND machine=$machine",
            row => row.Text(0), new SqlArg("$id", id), new("$machine", runtime.Machine)), context.RequestAborted);
        return JsonReply.WriteAsync(context, 409, json =>
        {
            json.WriteStartObject();
            json.WriteString("error", "stale");
            if (state == "superseded") json.WriteString("detail", WorkRedone);
            json.WriteEndObject();
        });
    }

    private const string WorkRedone = "Closed: the work it follows is being redone";

    internal sealed record Option(string Id, string Label, string Style, bool NeedsText = false);
    internal sealed record Item(string Id, string Kind, string SessionName, string? RunId, string? StepId,
        string Title, string Detail, string Provider, long CreatedAt, long Version, long Epoch, bool ProviderBacked, IReadOnlyList<Option> Options);

    internal static IReadOnlyList<Item> Read(IReadScope scope, string machine, string? id = null, string? sessionId = null) => scope.Query("""
        SELECT n.id,n.kind,CAST(s.title AS TEXT),n.run_id,n.step_id,n.text,s.provider,n.created_ms,n.version,s.epoch,
            n.options_json,r.payload,n.provider_request_id,n.source_kind
        FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id
        LEFT JOIN rows r ON r.session_id=n.session_id AND r.seq=(SELECT seq FROM rows WHERE session_id=n.session_id
            AND item_id='ask:'||n.chat_row_id AND kind='ask' ORDER BY seq DESC LIMIT 1)
        WHERE n.state='open' AND (n.addressee_session_id IS NULL OR
            (n.source_kind IN ('workflow','workflow_gate') AND n.addressee_session_id=n.session_id AND EXISTS
                (SELECT 1 FROM engine_run run WHERE run.id=n.run_id AND run.owner_session_id=n.session_id)))
            AND n.machine=$machine
            AND s.machine=$machine AND s.holder='local' AND s.reserved_epoch IS NULL
            AND s.state NOT IN ('foreign','handing_off','ended') AND ($id IS NULL OR n.id=$id)
            AND ($session IS NULL OR n.session_id=$session)
        ORDER BY n.created_ms DESC,n.id
        """, row =>
    {
        using var offered = JsonDocument.Parse(row.Text(10));
        using var card = row.IsNull(11) ? null : JsonDocument.Parse(row.Text(11));
        var source = row.IsNull(13) ? null : row.Text(13);
        var gate = source == "workflow_gate";
        var options = offered.RootElement.EnumerateArray().Select(value =>
        {
            var option = value.GetString()!;
            if (card is null && source is "workflow" or "workflow_gate") return WorkflowOption(option);
            JsonElement? chosen = null;
            if (card is not null && card.RootElement.TryGetProperty("options", out var actual) && actual.ValueKind == JsonValueKind.Array)
                foreach (var candidate in actual.EnumerateArray())
                    if (JsonReply.String(candidate, "optionId") == option) { chosen = candidate; break; }
            var kind = chosen is { } found ? JsonReply.String(found, "kind") : null;
            return new Option(option, chosen is { } label ? JsonReply.String(label, "name") ?? option : option,
                kind?.StartsWith("reject", StringComparison.Ordinal) == true ? "danger" : kind == "allow_once" ? "primary" : "neutral");
        }).ToArray();
        var title = card is null ? row.Text(5) : JsonReply.String(card.RootElement, "title") ?? row.Text(5);
        var detail = gate ? "" : card is null ? row.Text(5) : JsonReply.String(card.RootElement, "message") ?? "";
        return new Item(row.Text(0), gate ? "gate" : row.Text(1), row.Text(2), row.IsNull(3) ? null : row.Text(3),
            row.IsNull(4) ? null : row.Text(4), title, detail, row.Text(6), row.Long(7), row.Long(8), row.Long(9), !row.IsNull(12), options);
    }, new SqlArg("$machine", machine), new("$id", id), new("$session", sessionId));

    /// <summary>A workflow's own approval words. The engine offers ids; only a provider's question brings its own labels.</summary>
    private static Option WorkflowOption(string id) => id switch
    {
        "approve" => new(id, "Approve", "primary"),
        "allow_once" => new(id, "Allow once", "primary"),
        "revise" => new(id, "Revise", "neutral", NeedsText: true),
        "deny" => new(id, "Deny", "danger"),
        _ => new(id, id, "neutral")
    };

    internal static bool TryAnswer(JsonElement body, out string option, out long version)
    {
        option = JsonReply.String(body, "optionId") ?? "";
        version = 0;
        return !string.IsNullOrWhiteSpace(option) && body.TryGetProperty("version", out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out version) && version > 0;
    }

    internal static JsonElement AnswerFor(Item item, string option, string? text = null)
    {
        using var document = JsonDocument.Parse(JsonReply.Encode(json =>
        {
            if (item.ProviderBacked && item.Kind == "permission")
            { json.WriteStartObject(); json.WriteString("optionId", option); json.WriteEndObject(); }
            else if (text is not null)
            { json.WriteStartObject(); json.WriteString("choice", option); json.WriteString("text", text); json.WriteEndObject(); }
            else json.WriteStringValue(option);
        }));
        return document.RootElement.Clone();
    }

    internal static void WriteInbox(Utf8JsonWriter json, IReadOnlyList<Item> items)
    {
        json.WriteStartObject(); json.WritePropertyName("items");
        WriteItems(json, items);
        json.WriteEndObject();
    }

    internal static void WriteItems(Utf8JsonWriter json, IReadOnlyList<Item> items)
    {
        json.WriteStartArray();
        foreach (var item in items)
        {
            json.WriteStartObject();
            json.WriteString("id", item.Id); json.WriteString("kind", item.Kind);
            json.WriteString("sessionName", item.SessionName); json.WriteString("runId", item.RunId); json.WriteString("stepId", item.StepId);
            json.WriteString("title", item.Title); json.WriteString("detail", item.Detail);
            json.WriteNumber("createdAt", item.CreatedAt); json.WriteNumber("version", item.Version);
            json.WriteStartArray("evidence"); json.WriteStartObject();
            json.WriteString("label", "Provider"); json.WriteString("text", item.Provider);
            json.WriteEndObject(); json.WriteEndArray(); json.WriteStartArray("options");
            foreach (var option in item.Options)
            {
                json.WriteStartObject(); json.WriteString("id", option.Id); json.WriteString("label", option.Label);
                json.WriteString("style", option.Style);
                if (option.NeedsText) json.WriteBoolean("needsText", true);
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
