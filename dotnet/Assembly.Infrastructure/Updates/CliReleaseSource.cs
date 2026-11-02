using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Updates;

// This is npm stable release metadata, not evidence of an installed CLI's channel.
internal sealed record CliCandidate(string Provider, string Version, string Source, long CheckedMs);

internal sealed class CliReleaseSource(HttpClient http, TimeProvider time)
{
    private const int MetadataLimit = 64 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, Package> Packages = new Dictionary<string, Package>(StringComparer.Ordinal)
    {
        ["claude"] = new("@anthropic-ai/claude-code", "https://registry.npmjs.org/@anthropic-ai%2Fclaude-code/latest"),
        ["codex"] = new("@openai/codex", "https://registry.npmjs.org/@openai%2Fcodex/latest")
    };

    public async Task<CliCandidate> LatestAsync(string provider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (provider is null || !Packages.TryGetValue(provider, out var package))
            throw new CallFailed("CLI release check unsupported provider");

        using var timeout = new CancellationTokenSource(Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, package.Source);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.AbsoluteUri != package.Source ||
                response.Content.Headers.ContentLength > MetadataLimit)
                throw Unverified();

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var bytes = new byte[MetadataLimit + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, linked.Token);
            if (count > MetadataLimit) throw Unverified();
            using var json = JsonDocument.Parse(Utf8.GetString(bytes, 0, count));
            var version = ReadVersion(json.RootElement, package.Name);
            linked.Token.ThrowIfCancellationRequested();
            return new(provider, version, package.Source, time.GetUtcNow().ToUnixTimeMilliseconds());
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw Unverified();
        }
        catch (HttpRequestException) { throw Unverified(); }
        catch (IOException) { throw Unverified(); }
        catch (JsonException) { throw Unverified(); }
        catch (DecoderFallbackException) { throw Unverified(); }
    }

    private static string ReadVersion(JsonElement json, string package)
    {
        if (json.ValueKind != JsonValueKind.Object) throw Unverified();
        string? name = null, version = null;
        foreach (var property in json.EnumerateObject())
        {
            if (property.NameEquals("name"))
            {
                if (name is not null || property.Value.ValueKind != JsonValueKind.String) throw Unverified();
                name = property.Value.GetString();
            }
            else if (property.NameEquals("version"))
            {
                if (version is not null || property.Value.ValueKind != JsonValueKind.String) throw Unverified();
                version = property.Value.GetString();
            }
        }
        if (name != package || version is null || !IsStableVersion(version)) throw Unverified();
        return version;
    }

    private static bool IsStableVersion(string value)
    {
        var parts = value.Split('+');
        if (parts.Length > 2) return false;
        var numbers = parts[0].Split('.');
        if (numbers.Length != 3 || numbers.Any(n => n.Length == 0 || n.Length > 1 && n[0] == '0' || n.Any(c => c is < '0' or > '9')))
            return false;
        return parts.Length == 1 || parts[1].Split('.').All(p => p.Length > 0 && p.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-'));
    }

    private static CallFailed Unverified() => new("CLI release check unverified");
    private sealed record Package(string Name, string Source);
}
