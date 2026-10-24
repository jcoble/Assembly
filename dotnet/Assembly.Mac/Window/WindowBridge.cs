using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Assembly.Mac.Window;

/// <summary>
/// The page's host calls (api.ts <c>rpc()</c>), which the Tauri app answers over <c>invoke</c>. The page sends
/// <c>{id, method, params}</c> through <c>window.external.sendMessage</c>; the answer is <c>{id, result}</c> or
/// <c>{id, error}</c>. Methods the window can't answer yet return an error naming them. A message that isn't one gets no
/// answer.
/// </summary>
internal sealed class WindowBridge(string token, string soakResults, Uri backend, Func<string[]> pickImages)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly TurnActivity _activity = new();

    /// <summary>The images the user picked or dropped; only these can be uploaded or served.</summary>
    public ChosenImages Chosen { get; } = new();

    /// <summary>
    /// <c>attach://localhost/&lt;escaped path&gt;</c>: a chosen image, so the page can show the original. Any other
    /// path gets an empty answer, which the page shows as a broken image.
    /// </summary>
    public Stream OpenChosen(string url, out string contentType)
    {
        try
        {
            var (bytes, mime) = Chosen.Read(Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimStart('/')));
            contentType = mime;
            return new MemoryStream(bytes);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UriFormatException)
        {
            Console.WriteLine($"attach: refused ({e.GetType().Name})");
            contentType = "text/plain";
            return new MemoryStream();
        }
    }

    public async Task<string?> AnswerAsync(string message)
    {
        long id;
        string method;
        JsonElement parameters;
        try
        {
            using var request = JsonDocument.Parse(message);
            var root = request.RootElement;
            id = root.GetProperty("id").GetInt64();
            method = root.GetProperty("method").GetString() ?? "";
            parameters = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : default;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"bridge: not a message ({e.GetType().Name}, {message.Length} chars)");
            return null;
        }

        try
        {
            return await ReplyAsync(id, writer => CallAsync(writer, method, parameters));
        }
        catch (Exception e)
        {
            return await ReplyAsync(id, writer =>
            {
                writer.WriteString("error", e.Message);
                return Task.CompletedTask;
            });
        }
    }

    private async Task CallAsync(Utf8JsonWriter writer, string method, JsonElement parameters)
    {
        // The method only, never its params or answer: token.get's answer is the token.
        Console.WriteLine($"bridge: {method}");
        switch (method)
        {
            case "token.get":
                writer.WriteString("result", token);
                break;
            // The backend runs in this process, so it is up whenever the window is.
            case "backend.status":
                writer.WriteStartObject("result");
                writer.WriteString("state", "up");
                writer.WriteEndObject();
                break;
            case "memory.sample":
                MemorySample.Write(writer);
                break;
            // A turn started or ended (Conversation.svelte).
            case "activity.begin":
                _activity.Begin();
                writer.WriteNull("result");
                break;
            case "activity.end":
                _activity.End();
                writer.WriteNull("result");
                break;
            // { data: base64 image } -> base64 thumbnail (a pasted or HTML5-dropped image).
            case "attachment.thumbnail":
                var data = Convert.FromBase64String(String(parameters, "data"));
                if (data.Length is 0 or > 20 * 1024 * 1024)
                    throw new InvalidOperationException("Image must be between 1 byte and 20 MB");
                writer.WriteBase64String("result", Thumbnail.Make(data).Bytes);
                break;
            // { path } of a picked or dropped image -> { thumbnail: base64, size }; the page may then show the
            // original by attach:// URL.
            case "attachment.thumbnail_path":
                var (original, _) = Chosen.Read(String(parameters, "path"));
                writer.WriteStartObject("result");
                writer.WriteBase64String("thumbnail", Thumbnail.Make(original).Bytes);
                writer.WriteNumber("size", original.Length);
                writer.WriteEndObject();
                break;
            // The native picker for images; the chosen paths ([] on cancel).
            case "attachment.pick":
                var picked = pickImages();
                Chosen.Add(picked);
                writer.WriteStartArray("result");
                foreach (var path in picked)
                    writer.WriteStringValue(path);
                writer.WriteEndArray();
                break;
            // { threadId, path } -> { path } where the agent runs; the file goes to the backend from here, so the
            // original never crosses into the page.
            case "attachment.upload_path":
                writer.WritePropertyName("result");
                writer.WriteRawValue(await UploadAsync(String(parameters, "threadId"), String(parameters, "path")));
                break;
            // { path } the composer removed before sending.
            case "attachment.discard":
                Chosen.Forget(String(parameters, "path"));
                writer.WriteNull("result");
                break;
            // The absolute paths of what was just dropped on the page, files and folders alike.
            case "drop.paths":
                writer.WriteStartArray("result");
                foreach (var path in DropPaths.Read())
                    writer.WriteStringValue(path);
                writer.WriteEndArray();
                break;
            case "soak.append":
                SoakAppend(parameters);
                writer.WriteNull("result");
                break;
            default:
                throw new InvalidOperationException($"{method} isn't in the Photino window yet");
        }
    }

    // POST /threads/:id/attachments, as the Tauri app does. The composer discards the path once the whole send has
    // uploaded, so a retry after a partial failure still works.
    private async Task<string> UploadAsync(string threadId, string path)
    {
        if (threadId.Length == 0 || !threadId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new InvalidOperationException("bad thread id");
        var (bytes, mime) = Chosen.Read(path);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(backend, $"threads/{threadId}/attachments"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"upload returned {(int)response.StatusCode} {response.ReasonPhrase}");
        return await response.Content.ReadAsStringAsync();
    }

    private static string String(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.GetString() is { } text
            ? text
            : throw new InvalidOperationException($"needs {{ {name} }}");

    // One CSV line appended to soak-results/<name>, as the Tauri app does.
    private void SoakAppend(JsonElement parameters)
    {
        var name = parameters.ValueKind == JsonValueKind.Object ? parameters.GetProperty("name").GetString() : null;
        var line = parameters.ValueKind == JsonValueKind.Object ? parameters.GetProperty("line").GetString() : null;
        if (string.IsNullOrEmpty(name) || line is null || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException("soak.append needs { name, line } with a plain file name");
        Directory.CreateDirectory(soakResults);
        File.AppendAllText(Path.Combine(soakResults, name), line + "\n");
    }

    private static async Task<string> ReplyAsync(long id, Func<Utf8JsonWriter, Task> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            await body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
