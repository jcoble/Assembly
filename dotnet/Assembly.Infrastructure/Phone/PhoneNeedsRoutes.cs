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
            var id = context.Request.RouteValues["id"] as string;
            var item = ((IEngineRuntime)runtime).Read(scope => Read(scope, runtime.Machine, id).SingleOrDefault(), context.RequestAborted);
            if (item is null || item.Version != version)
            { await JsonReply.ErrorAsync(context, 409, "stale"); return; }
            if (!item.Options.Any(offered => offered.Id == option))
            { await JsonReply.ErrorAsync(context, 400, "That option is not offered for this item"); return; }
            var answer = AnswerFor(item, option);
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
                var stale = result.Error is "already_resolved" or "version_conflict" or "stale_epoch" or "stale_attempt";
                await JsonReply.ErrorAsync(context, stale ? 409 : 400, stale ? "stale" : result.Error ?? "answer refused");
            }
            catch (UnauthorizedAccessException)
            {
                await JsonReply.ErrorAsync(context, 401, "Pair this phone again");
            }
        });
    }

    internal sealed record Option(string Id, string Label, string Style);
    internal sealed record Item(string Id, string Kind, string SessionName, string? RunId, string? StepId,
        string Title, string Detail, string Provider, long CreatedAt, long Version, long Epoch, bool ProviderBacked, IReadOnlyList<Option> Options);

    internal static IReadOnlyList<Item> Read(IReadScope scope, string machine, string? id = null, string? sessionId = null) => scope.Query("""
        SELECT n.id,n.kind,CAST(s.title AS TEXT),n.run_id,n.step_id,n.text,s.provider,n.created_ms,n.version,s.epoch,
            n.options_json,r.payload,n.provider_request_id
        FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id
        LEFT JOIN rows r ON r.session_id=n.session_id AND r.seq=(SELECT seq FROM rows WHERE session_id=n.session_id
            AND item_id='ask:'||n.chat_row_id AND kind='ask' ORDER BY seq DESC LIMIT 1)
        WHERE n.state='open' AND (n.addressee_session_id IS NULL OR
            (n.source_kind='workflow' AND n.addressee_session_id=n.session_id AND EXISTS
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
        var options = offered.RootElement.EnumerateArray().Select(value =>
        {
            var option = value.GetString()!;
            JsonElement? source = null;
            if (card is not null && card.RootElement.TryGetProperty("options", out var actual) && actual.ValueKind == JsonValueKind.Array)
                foreach (var candidate in actual.EnumerateArray())
                    if (JsonReply.String(candidate, "optionId") == option) { source = candidate; break; }
            var kind = source is { } found ? JsonReply.String(found, "kind") : null;
            return new Option(option, source is { } label ? JsonReply.String(label, "name") ?? option : option,
                kind?.StartsWith("reject", StringComparison.Ordinal) == true ? "danger" : kind == "allow_once" ? "primary" : "neutral");
        }).ToArray();
        var title = card is null ? row.Text(5) : JsonReply.String(card.RootElement, "title") ?? row.Text(5);
        var detail = card is null ? row.Text(5) : JsonReply.String(card.RootElement, "message") ?? "";
        return new Item(row.Text(0), row.Text(1), row.Text(2), row.IsNull(3) ? null : row.Text(3),
            row.IsNull(4) ? null : row.Text(4), title, detail, row.Text(6), row.Long(7), row.Long(8), row.Long(9), !row.IsNull(12), options);
    }, new SqlArg("$machine", machine), new("$id", id), new("$session", sessionId));

    internal static bool TryAnswer(JsonElement body, out string option, out long version)
    {
        option = JsonReply.String(body, "optionId") ?? "";
        version = 0;
        return !string.IsNullOrWhiteSpace(option) && body.TryGetProperty("version", out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out version) && version > 0;
    }

    internal static JsonElement AnswerFor(Item item, string option)
    {
        using var document = JsonDocument.Parse(JsonReply.Encode(json =>
        {
            if (item.ProviderBacked && item.Kind == "permission")
            { json.WriteStartObject(); json.WriteString("optionId", option); json.WriteEndObject(); }
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
                json.WriteString("style", option.Style); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
