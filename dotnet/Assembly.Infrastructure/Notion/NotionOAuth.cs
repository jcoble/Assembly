using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Notion;

namespace Assembly.Infrastructure.Notion;

/// <summary>One owned attempt; callbacks never receive or supply the verifier.</summary>
public sealed class NotionOAuth(HttpClient client, NotionSettings settings, TimeProvider time) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private Attempt? _attempt;
    private bool _disposed;
    private sealed class Attempt(TimeProvider time) : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("D");
        public string State { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        public byte[] Verifier { get; } = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        public CancellationTokenSource Stop { get; } = new(TimeSpan.FromSeconds(300), time);
        public void Dispose() { Stop.Cancel(); Stop.Dispose(); CryptographicOperations.ZeroMemory(Verifier); }
    }
    public async Task<NotionOAuthStart> BeginAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _attempt?.Dispose();
            var a = _attempt = new Attempt(time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, a.Stop.Token);
            var body = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["state"] = a.State, ["verifierHash"] = Convert.ToHexStringLower(SHA256.HashData(a.Verifier)) }, NotionJson.Default.DictionaryStringString);
            var result = await PostAsync("start", body, linked.Token);
            var url = result.GetProperty("authorizationUrl").GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.Host != "api.notion.com" || parsed.AbsolutePath != "/v1/oauth/authorize")
                throw new NotionException("invalid_authorization_url");
            return new(a.Id, url, a.State);
        }
        catch { _attempt?.Dispose(); _attempt = null; throw; }
        finally { _gate.Release(); }
    }
    public async Task<NotionSettingsView> ClaimAsync(string operationId, string state, string claimCode, CancellationToken ct)
    {
        var staged = await StageClaimAsync(operationId, state, claimCode, ct);
        try { return settings.Commit(staged); }
        finally { staged.Dispose(); await settings.ReconcileAsync(CancellationToken.None); }
    }
    public async Task<NotionSettings.Change> StageClaimAsync(string operationId, string state, string claimCode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var a = _attempt;
            if (a is null || a.Id != operationId || a.State != state || a.Stop.IsCancellationRequested) throw new NotionException("stale_oauth");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, a.Stop.Token);
            var body = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["state"] = state, ["verifier"] = Encoding.ASCII.GetString(a.Verifier), ["claimCode"] = claimCode }, NotionJson.Default.DictionaryStringString);
            var result = await PostAsync("claim", body, linked.Token);
            var token = result.GetProperty("accessToken").GetString();
            var view = await settings.StageAsync(null, token, linked.Token);
            a.Dispose(); _attempt = null;
            return view;
        }
        finally { _gate.Release(); }
    }
    public void Cancel(string operationId)
    {
        // Cancellation is synchronous and interrupts an in-flight claim before it may commit.
        var a = _attempt;
        if (a?.Id == operationId) a.Stop.Cancel();
    }
    private async Task<JsonElement> PostAsync(string action, JsonElement body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("https://auth.coblesolutions.com/notion/" + action, content, ct);
            if (!response.IsSuccessStatusCode) throw new NotionException("oauth_failed");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 65536) throw new NotionException("oauth_invalid_response");
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new NotionException("oauth_transport"); }
        catch (JsonException) { throw new NotionException("oauth_invalid_response"); }
    }
    public async ValueTask DisposeAsync()
    {
        _attempt?.Stop.Cancel();
        await _gate.WaitAsync();
        try { _disposed = true; _attempt?.Dispose(); _attempt = null; }
        finally { _gate.Release(); }
    }
}
