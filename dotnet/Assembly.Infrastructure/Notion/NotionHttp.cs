using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assembly.Application.Features.Notion;

namespace Assembly.Infrastructure.Notion;

public sealed partial class NotionHttp(HttpClient client) : INotionHttp
{
    private NotionSettings? _settings;
    public void Bind(NotionSettings settings) => _settings = settings;
    [GeneratedRegex(@"^/v1/(search|pages(/[0-9a-fA-F-]{32,36})?|blocks/[0-9a-fA-F-]{32,36}(/children)?|data_sources/[0-9a-fA-F-]{32,36}(/query)?|comments|file_uploads(/[0-9a-fA-F-]{32,36}(/send|/complete)?)?)(\?[^\s#]*)?$")]
    private static partial Regex ApiPath();
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonElement? body, CancellationToken ct)
    {
        var settings = _settings ?? throw new NotionException("notion_unconfigured");
        var generation = settings.Read().Generation;
        var token = await settings.TokenAsync(generation, ct);
        try { var result = await SendWithTokenAsync(method, path, body, token, ct); settings.Check(generation); return result; }
        finally { CryptographicOperations.ZeroMemory(token); }
    }
    public async Task<JsonElement> SendWithTokenAsync(HttpMethod method, string path, JsonElement? body, byte[] token, CancellationToken ct)
    {
        if (token.Length is 0 or > 65536 || token.Any(b => b is < 0x21 or > 0x7e)) throw new NotionException("invalid_token");
        if (!ApiPath().IsMatch(path)) throw new NotionException("invalid_notion_path");
        using var request = new HttpRequestMessage(method, new Uri("https://api.notion.com" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(token));
        request.Headers.Add("Notion-Version", "2026-03-11");
        if (body is { } value) request.Content = new StringContent(value.GetRawText(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new NotionException("notion_http_" + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new NotionException("notion_too_large");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + count > 2 * 1024 * 1024) throw new NotionException("notion_too_large");
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            return json.RootElement.Clone();
        }
        catch (IOException) { throw new NotionException("notion_transport"); }
        catch (HttpRequestException) { throw new NotionException("notion_transport"); }
        catch (JsonException) { throw new NotionException("notion_invalid_response"); }
    }

    public async Task SendUploadAsync(string id, Stream stream, string mime, CancellationToken ct)
    {
        id = NotionIds.Canonical(id);
        var settings = _settings ?? throw new NotionException("notion_unconfigured");
        var generation = settings.Read().Generation;
        var token = await settings.TokenAsync(generation, ct);
        try
        {
            if (token.Any(b => b is < 0x21 or > 0x7e)) throw new NotionException("invalid_token");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.notion.com/v1/file_uploads/" + id + "/send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(token));
            request.Headers.Add("Notion-Version", "2026-03-11");
            using var content = new MultipartFormDataContent();
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue(mime);
            content.Add(file, "file", "screenshot"); request.Content = content;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new NotionException("notion_http_" + (int)response.StatusCode);
            settings.Check(generation);
        }
        catch (HttpRequestException) { throw new NotionException("notion_transport"); }
        catch (IOException) { throw new NotionException("notion_transport"); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }
}
