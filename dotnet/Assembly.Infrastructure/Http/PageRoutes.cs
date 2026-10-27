using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Assembly.Infrastructure.Diagnostics;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// The page's calls to the desktop it runs on (app/src/lib/host/call.ts): <c>POST /page/:method</c> with the params as
/// the body answers <c>{result}</c>, or 400 <c>{error}</c>; an unknown method is 404. The shell's native jobs (the
/// picker, dropped paths, links, notifications, the clipboard) aren't here.
/// </summary>
internal sealed class PageRoutes(string token, string soakResults, Func<Uri> backend)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly TurnActivity _activity = new();

    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/page/{method}", CallAsync);

    private async Task CallAsync(HttpContext context)
    {
        var method = context.Request.RouteValues["method"] as string ?? "";
        // The method only, never its params or answer.
        Console.WriteLine($"page: {method}");
        using var body = await JsonDocument.ParseAsync(context.Request.Body).ContinueWith(read => read.IsCompletedSuccessfully ? read.Result : null);
        var parameters = body?.RootElement is { ValueKind: JsonValueKind.Object } root ? root : default;
        try
        {
            if (await ResultAsync(method, parameters) is not { } result)
            {
                await Reply(context, StatusCodes.Status404NotFound, writer => writer.WriteString("error", $"{method} isn't a page call"));
                return;
            }
            await Reply(context, StatusCodes.Status200OK, result);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FormatException or KeyNotFoundException)
        {
            await Reply(context, StatusCodes.Status400BadRequest, writer => writer.WriteString("error", e.Message));
        }
    }

    // The writer of the answer's "result", or null for an unknown method.
    private async Task<Action<Utf8JsonWriter>?> ResultAsync(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "memory.sample":
                return MemorySample.Write;
            // A turn started or ended (Conversation.svelte).
            case "activity.begin":
                _activity.Begin();
                return Null;
            case "activity.end":
                _activity.End();
                return Null;
            // { data: base64 image } -> base64 thumbnail (a pasted or HTML5-dropped image).
            case "attachment.thumbnail":
                var data = Convert.FromBase64String(String(parameters, "data"));
                if (data.Length is 0 or > 20 * 1024 * 1024)
                    throw new InvalidOperationException("Image must be between 1 byte and 20 MB");
                var made = Thumbnail.Make(data).Bytes;
                return writer => writer.WriteBase64String("result", made);
            // { path } of a picked or dropped image -> { thumbnail: base64, size }.
            case "attachment.thumbnail_path":
                var (original, _) = ImageFile.Read(String(parameters, "path"));
                var thumbnail = Thumbnail.Make(original).Bytes;
                return writer =>
                {
                    writer.WriteStartObject("result");
                    writer.WriteBase64String("thumbnail", thumbnail);
                    writer.WriteNumber("size", original.Length);
                    writer.WriteEndObject();
                };
            // { sessionId, path } -> { path } where the agent runs; the file goes from here, so it never crosses into
            // the page.
            case "attachment.upload_path":
                var uploaded = await UploadAsync(String(parameters, "sessionId"), String(parameters, "path"));
                return writer =>
                {
                    writer.WritePropertyName("result");
                    writer.WriteRawValue(uploaded);
                };
            case "soak.append":
                SoakAppend(String(parameters, "name"), String(parameters, "line"));
                return Null;
            default:
                return null;
        }
    }

    private static void Null(Utf8JsonWriter writer) => writer.WriteNull("result");

    // POST /sessions/:id/attachments on this backend, which sends it on to the session's machine.
    private async Task<string> UploadAsync(string sessionId, string path)
    {
        if (sessionId.Length == 0 || !sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new InvalidOperationException("bad session id");
        var (bytes, mime) = ImageFile.Read(path);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(backend(), $"sessions/{sessionId}/attachments"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"upload returned {(int)response.StatusCode} {response.ReasonPhrase}");
        return await response.Content.ReadAsStringAsync();
    }

    // One CSV line appended to soak-results/<name>.
    private void SoakAppend(string name, string line)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException("soak.append needs { name, line } with a plain file name");
        Directory.CreateDirectory(soakResults);
        File.AppendAllText(Path.Combine(soakResults, name), line + "\n");
    }

    private static string String(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.GetString() is { } text
            ? text
            : throw new InvalidOperationException($"needs {{ {name} }}");

    private static async Task Reply(HttpContext context, int status, Action<Utf8JsonWriter> body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        body(writer);
        writer.WriteEndObject();
    }
}
