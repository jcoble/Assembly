using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// <c>POST /threads/:id/attachments</c> takes one image's raw bytes, stores it where the thread's agent runs, and answers
/// with its path there; <c>GET /threads/:id/attachments/:name</c> reads this machine's copy back for the preview.
/// </summary>
internal sealed class AttachmentRoutes(ThreadStore threads, MachineSet machines, AttachmentStore attachments)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/threads/{id}/attachments", SaveAsync);
        routes.MapGet("/threads/{id}/attachments/{name}", ReadAsync);
    }

    private async Task SaveAsync(HttpContext context)
    {
        if (threads.Get(RequestValues.Route(context, "id")) is not { } thread)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown thread");
            return;
        }

        if (ImageType.ForContentType(context.Request.ContentType) is not { } type)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "expected a png, jpeg, gif or webp image");
            return;
        }

        var bytes = await ReadBodyAsync(context);
        if (bytes is null)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "images must be 20 MB or smaller");
            return;
        }

        if (!type.Matches(bytes.Value.Span))
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "the bytes are not the image type sent");
            return;
        }

        string path;
        try
        {
            path = await machines.For(thread).SaveAttachmentAsync(thread, type, bytes.Value, context.RequestAborted);
        }
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteString("path", path);
            json.WriteEndObject();
        });
    }

    private async Task ReadAsync(HttpContext context)
    {
        var threadId = RequestValues.Route(context, "id");
        var name = RequestValues.Route(context, "name");
        if (AttachmentStore.TypeOfName(name) is not { } type)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "not an attachment name");
            return;
        }

        var path = attachments.PathOf(threadId, name);
        if (threads.Get(threadId) is null || !File.Exists(path))
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "no such attachment");
            return;
        }

        context.Response.ContentType = type.ContentType;
        await context.Response.SendFileAsync(path, context.RequestAborted);
    }

    /// <summary>The whole body, or null once it passes the size limit.</summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (body.Length + read > AttachmentStore.MaxBytes)
                return null;
            body.Write(chunk, 0, read);
        }

        return body.GetBuffer().AsMemory(0, (int)body.Length);
    }
}
