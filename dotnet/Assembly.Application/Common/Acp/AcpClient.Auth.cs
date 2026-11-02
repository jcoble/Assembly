using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp;

public sealed partial class AcpClient
{
    private IReadOnlyList<AuthMethod> _authMethods = [];
    private readonly Lock _authGate = new();
    private readonly Dictionary<long, LoginAttempt> _logins = [];
    private readonly Dictionary<string, UrlInteraction> _urlInteractions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _authClosing = new();
    private readonly BackgroundWork _authWork = new(TimeProvider.System, TextWriter.Null);
    private bool _authClosed;
    public event Action<AuthStatus>? AuthStatusChanged;

    private sealed class LoginAttempt(Action<Uri> present, Func<Uri, CancellationToken, ValueTask<bool>>? consent, CancellationToken ct)
    {
        public long Id;
        public bool Active = true;
        public Action<Uri> Present { get; } = present;
        public Func<Uri, CancellationToken, ValueTask<bool>>? Consent { get; } = consent;
        public CancellationToken Cancellation { get; } = ct;
    }

    private sealed class UrlInteraction(string id, LoginAttempt? login, CancellationTokenSource closing)
    {
        public string Id { get; } = id;
        public LoginAttempt? Login { get; } = login;
        public CancellationTokenSource Closing { get; } = closing;
        public CancellationToken Token { get; } = closing.Token;
        public bool Active = true;
    }

    public Task LoginAsync(string methodId, Action<Uri> onUrl, CancellationToken ct) => LoginCoreAsync(methodId, onUrl, null, ct);

    public Task LoginAsync(string methodId, Action<Uri> onUrl, Func<Uri, CancellationToken, ValueTask<bool>> awaitUrlConsent, CancellationToken ct) =>
        LoginCoreAsync(methodId, onUrl, awaitUrlConsent, ct);

    private async Task LoginCoreAsync(string methodId, Action<Uri> onUrl, Func<Uri, CancellationToken, ValueTask<bool>>? consent, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(methodId) || !_authMethods.Any(method => method.MethodId == methodId && method.Type == "agent"))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "This authentication method does not support auth/login.");
        ct.ThrowIfCancellationRequested();
        var attempt = new LoginAttempt(onUrl, consent, ct);
        try
        {
            var result = await _rpc.RequestAsync(AcpMethods.AuthLogin,
                JsonSerializer.SerializeToElement(new AuthLoginRequest(methodId), AcpJsonContext.Default.AuthLoginRequest), ct,
                onRequest: id =>
                {
                    lock (_authGate)
                    {
                        attempt.Id = id;
                        if (!_authClosed) _logins.Add(id, attempt);
                    }
                }, onSettled: () => RetireLogin(attempt));
            ct.ThrowIfCancellationRequested();
            if (result.ValueKind != JsonValueKind.Object)
                throw new JsonRpcException(JsonRpcException.InternalError, "auth/login returned an invalid result.");
            _ = result.Deserialize(AcpJsonContext.Default.AuthResponse);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelLogin(attempt.Id);
            throw;
        }
        finally { RetireLogin(attempt); }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (_authMethods.Count == 0) return;
        await RequestAsync(AcpMethods.AuthLogout, new AuthLogoutRequest(), AcpJsonContext.Default.AuthLogoutRequest, AcpJsonContext.Default.AuthResponse, ct);
    }

    private void CancelLogin(long id)
    {
        lock (_authGate)
        {
            if (_authClosed || id == 0) return;
            _authWork.Start("ACP login cancellation", async closing =>
            {
                try
                {
                    await _rpc.NotifyAsync("$/cancel_request", JsonSerializer.SerializeToElement(new AuthCancelRequest(id),
                        AcpJsonContext.Default.AuthCancelRequest), closing);
                }
                catch (Exception) { } // Best effort on this connection; transport messages can contain secrets.
            });
        }
    }

    private void RetireLogin(LoginAttempt attempt)
    {
        UrlInteraction[] retired;
        lock (_authGate)
        {
            if (!attempt.Active) return;
            attempt.Active = false;
            _logins.Remove(attempt.Id);
            retired = _urlInteractions.Values.Where(url => ReferenceEquals(url.Login, attempt)).ToArray();
            foreach (var url in retired) { url.Active = false; _urlInteractions.Remove(url.Id); }
        }
        foreach (var url in retired) StopUrl(url);
    }

    private void StopUrl(UrlInteraction interaction) => _authWork.Start("ACP URL cleanup", async _ =>
    {
        // CancelAsync marks cancellation before returning, but never blocks the reader on user callbacks.
        try { await interaction.Closing.CancelAsync(); }
        catch (Exception) { }
        finally { interaction.Closing.Dispose(); }
    });

    private void RetireUrl(string id, UrlInteraction? expected = null)
    {
        UrlInteraction? interaction;
        lock (_authGate)
        {
            if (!_urlInteractions.TryGetValue(id, out interaction)
                || (expected is not null && !ReferenceEquals(interaction, expected))) return;
            _urlInteractions.Remove(id);
            interaction.Active = false;
        }
        StopUrl(interaction);
    }

    private bool UrlIsCurrent(UrlInteraction interaction)
    {
        lock (_authGate) return interaction.Active && !interaction.Token.IsCancellationRequested
            && _urlInteractions.TryGetValue(interaction.Id, out var current) && ReferenceEquals(current, interaction)
            && (interaction.Login is not { } login || (login.Active && !login.Cancellation.IsCancellationRequested));
    }

    private async Task<ElicitationResponse> AnswerUrlAsync(ElicitationRequest question, JsonRpcRequestContext? context, CancellationToken connectionClosing)
    {
        if (string.IsNullOrEmpty(question.ElicitationId) || !Uri.TryCreate(question.Url, UriKind.Absolute, out var url))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid URL elicitation.");
        UrlInteraction interaction;
        lock (_authGate)
        {
            if (_authClosed || _urlInteractions.ContainsKey(question.ElicitationId)) return ElicitationResponse.Cancel;
            LoginAttempt? login = null;
            if (string.IsNullOrEmpty(question.SessionId))
            {
                if (question.RequestId is not { ValueKind: JsonValueKind.Number } request || !request.TryGetInt64(out var id)
                    || !_logins.TryGetValue(id, out login) || !login.Active || login.Cancellation.IsCancellationRequested)
                    return ElicitationResponse.Cancel;
            }
            var closing = CancellationTokenSource.CreateLinkedTokenSource(connectionClosing, _authClosing.Token, login?.Cancellation ?? default);
            interaction = new(question.ElicitationId, login, closing);
            _urlInteractions.Add(interaction.Id, interaction);
        }
        try
        {
            ElicitationResponse answer;
            if (interaction.Login is { } login)
            {
                if (!UrlIsCurrent(interaction)) return ElicitationResponse.Cancel;
                login.Present(url);
                var accepted = login.Consent is not null && UrlIsCurrent(interaction)
                    && await login.Consent(url, interaction.Token).AsTask().WaitAsync(interaction.Token);
                answer = new(accepted ? "accept" : "cancel");
            }
            else
            {
                answer = await AskAsync(question.SessionId!, ElicitationResponse.Cancel,
                    async (policy, ct) => new ElicitationResponse(await policy.ElicitUrlAsync(question.SessionId, url, url.Host, ct).AsTask().WaitAsync(ct) ? "accept" : "cancel"),
                    interaction.Token, context, AcpMethods.ElicitationCreate,
                    value => JsonSerializer.SerializeToElement(value, AcpJsonContext.Default.ElicitationResponse));
            }
            if (answer.Action == "accept" && UrlIsCurrent(interaction)) return answer;
        }
        catch (Exception) { } // A presentation/consent/policy failure must never echo the original URL.
        finally
        {
            // Accepted interactions remain reserved until complete or their login/connection ends.
            if (!UrlIsCurrent(interaction)) RetireUrl(interaction.Id, interaction);
        }
        RetireUrl(interaction.Id, interaction);
        return ElicitationResponse.Cancel;
    }

    private void CompleteElicitation(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("elicitationId", out var id)
            && id.ValueKind == JsonValueKind.String && id.GetString() is { } key) RetireUrl(key);
    }

    private void CloseAuthentication()
    {
        UrlInteraction[] retired;
        lock (_authGate)
        {
            if (_authClosed) return;
            _authClosed = true;
            foreach (var login in _logins.Values) login.Active = false;
            _logins.Clear();
            retired = _urlInteractions.Values.ToArray();
            foreach (var url in retired) url.Active = false;
            _urlInteractions.Clear();
        }
        _authWork.Start("ACP auth close", async _ => { try { await _authClosing.CancelAsync(); } catch (Exception) { } });
        foreach (var url in retired) StopUrl(url);
    }

    private void AuthStatusUpdated(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("authStatus", out var status)
            || status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return;
        string? label = null;
        if (status.TryGetProperty("label", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String) return;
            label = value.GetString();
        }
        AuthStatusChanged?.Invoke(new(kind.GetString()!, label));
    }
}
