using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using CallFailed = Assembly.Application.Common.Calls.CallFailed;

namespace Assembly.Infrastructure.Phone;

/// <summary>The host supplies the same keyed ordering used by its session calls.</summary>
public delegate Task<OwnerPromptAccepted> PhoneChatOrder(string key,
    Func<Task<OwnerPromptAccepted>> run, CancellationToken cancellation);

/// <summary>The retained phone HTTP send, using the owning Engine's prepared command and writer.</summary>
public static class PhoneChatRoutes
{
    internal const int MaxBodyBytes = 64 * 1024;
    internal const int MaxTextCharacters = 10_000;
    internal const int MaxAttachments = 8;

    public static void Map(IEndpointRouteBuilder endpoints, IEngineDoor door, IAtomicWriter writer,
        Func<HttpContext, PhonePeer?> authenticatedPeer, PhoneChatOrder order,
        Action<IWriteScope, PhonePeer> ensureCurrentPeer, Func<string, bool> isLocalSession) =>
        endpoints.MapPost("/phone/v1/sessions/{id}/chat", (HttpContext context) =>
            HandleAsync(context, authenticatedPeer(context), door, writer, order, ensureCurrentPeer, isLocalSession));

    // A peer comes only from admission. Neither a request body nor a missing context becomes the owner.
    internal static async Task HandleAsync(HttpContext context, PhonePeer? peer, IEngineDoor door,
        IAtomicWriter writer, PhoneChatOrder order, Action<IWriteScope, PhonePeer> ensureCurrentPeer,
        Func<string, bool> isLocalSession)
    {
        if (peer is null || string.IsNullOrWhiteSpace(peer.DeviceId))
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status401Unauthorized, "Pair this phone again");
            return;
        }
        try
        {
            var session = context.Request.RouteValues["id"] as string;
            if (string.IsNullOrWhiteSpace(session)) throw new CallBadArguments("A session is required");
            var request = await ReadAsync(context, session);
            var accepted = await order(SendFeature.OrderOf(session), async () =>
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                // A paired phone operates this host directly, never through its desktop remote forwarding door.
                if (!isLocalSession(session)) throw new KeyNotFoundException("unknown session");
                var apply = await door.PrepareOwnerPromptAsync(request, context.RequestAborted);
                context.RequestAborted.ThrowIfCancellationRequested();
                return writer.Write("phone/chat", scope =>
                {
                    ensureCurrentPeer(scope, peer);
                    return apply(scope);
                });
            }, context.RequestAborted);
            await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
            {
                json.WriteStartObject();
                json.WriteNull("itemId");
                json.WriteString("sessionId", accepted.SessionId);
                json.WriteString("messageId", accepted.MessageId);
                json.WriteString("clientId", accepted.ClientId);
                json.WriteString("state", accepted.State);
                json.WriteNumber("messageVersion", accepted.MessageVersion);
                json.WriteNumber("seq", accepted.Seq);
                json.WriteNumber("epoch", accepted.Epoch);
                if (accepted.RunId is null) json.WriteNull("runId");
                else json.WriteString("runId", accepted.RunId);
                json.WriteEndObject();
            });
        }
        catch (CallBadArguments error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, error.Message);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "The message body is over 64 KiB");
        }
        catch (KeyNotFoundException)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown session");
        }
        catch (UnauthorizedAccessException)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status401Unauthorized, "Pair this phone again");
        }
        catch (CallFailed error)
        {
            var status = error.Message == "unknown session" ? StatusCodes.Status404NotFound
                : error.Message == "client_id_conflict" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            await JsonReply.ErrorAsync(context, status, error.Message);
        }
    }

    private static async Task<OwnerPromptRequest> ReadAsync(HttpContext context, string session)
    {
        if (context.Request.ContentLength > MaxBodyBytes) throw TooLarge();
        // Bound chunked bodies too, before allocating a JSON document. The extra byte detects overflow.
        var bytes = new byte[MaxBodyBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), context.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxBodyBytes) throw TooLarge();
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object || JsonReply.String(body, "text") is not { } text)
                throw new CallBadArguments("A message requires text");
            var clientId = JsonReply.String(body, "clientId");
            if (SendFeature.ClientIdError(clientId) is { } error) throw new CallBadArguments(error);
            var attachments = new List<string>();
            if (body.TryGetProperty("attachmentIds", out var references))
            {
                if (references.ValueKind != JsonValueKind.Array) throw new CallBadArguments("attachmentIds must be an array");
                foreach (var reference in references.EnumerateArray())
                {
                    if (reference.ValueKind != JsonValueKind.String || reference.GetString() is not { } id || string.IsNullOrEmpty(id))
                        throw new CallBadArguments("attachmentIds must contain image IDs");
                    attachments.Add(id);
                    if (attachments.Count > MaxAttachments) throw new CallBadArguments("A message carries at most 8 images");
                }
            }
            if ((string.IsNullOrWhiteSpace(text) && attachments.Count == 0) || text.EnumerateRunes().Count() > MaxTextCharacters)
                throw new CallBadArguments("A message must be 1 to 10,000 characters");
            long? epoch = null;
            if (body.TryGetProperty("epoch", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
            {
                if (supplied.ValueKind != JsonValueKind.Number || !supplied.TryGetInt64(out var value) || value < 0)
                    throw new CallBadArguments("epoch must be nonnegative");
                epoch = value;
            }
            return new(session, text, clientId!, attachments.ToArray(), epoch, LocalOnly: true);
        }
        catch (JsonException)
        {
            throw new CallBadArguments("The message body must be a JSON object");
        }
    }

    private static BadHttpRequestException TooLarge() => new("The message body is over 64 KiB", StatusCodes.Status413PayloadTooLarge);
}
