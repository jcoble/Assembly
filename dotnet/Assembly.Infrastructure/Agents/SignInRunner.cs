using System.Collections.ObjectModel;
using System.Text;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Agents;

namespace Assembly.Infrastructure.Agents;

public enum SignInEnd { SignedIn, Cancelled, Refused, AdapterCrashed, LinkLost, Failed }

public interface ISignInSession : IAsyncDisposable
{
    string OperationId { get; }
    Task<SignInEnd> Completion { get; }
    Task CancelAsync();
}

/// <summary>Each handle owns one lazy, dedicated sign-in attempt. Effects register it before reading Completion.</summary>
public sealed class SignInRunner(ChallengeBoard board, IProgramTerminals terminals, IAgentLauncher launcher,
    string probeFolder, TimeProvider time, TextWriter log)
{
    private static readonly Implementation ClientInfo = new("assembly-backend", "0.1.0");
    private readonly ChallengeBoard _board = board;
    private readonly IProgramTerminals _terminals = terminals;
    private readonly IAgentLauncher _launcher = launcher;
    private readonly string _probeFolder = probeFolder;
    private readonly TimeProvider _time = time;
    private readonly TextWriter _log = log;

    public Task<ISignInSession> StartAsync(AgentDescriptor agent, string? selectedMethodId, string operationId, CancellationToken ct)
    {
        // The effect already captured the complete environment at its target check. Never read ambient state again.
        var environment = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(agent.Env ?? new Dictionary<string, string>()));
        var captured = agent with { Args = agent.Args.ToArray(), Env = environment };
        return Task.FromResult<ISignInSession>(new Session(this, captured, environment, selectedMethodId, operationId, ct));
    }

    private sealed class DenyPermission : IPermissionPolicy
    {
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken ct) =>
            Task.FromResult(PermissionOutcome.Cancelled);
    }

    private sealed class Session(SignInRunner runner, AgentDescriptor agent, IReadOnlyDictionary<string, string> environment,
        string? selectedMethodId, string operationId, CancellationToken ct) : ISignInSession
    {
        private readonly SignInRunner _runner = runner;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        private Task<SignInEnd>? _completion;
        private IAgentLink? _link;
        private string? _terminal;
        private TerminalInput? _input;
        private bool _closing;
        public string OperationId => operationId;

        public Task<SignInEnd> Completion
        {
            get
            {
                lock (_gate)
                    return _completion ??= _stop.IsCancellationRequested ? Task.FromResult(SignInEnd.Cancelled) : RunAsync();
            }
        }

        public async Task CancelAsync()
        {
            Task<SignInEnd> completion;
            lock (_gate)
                completion = _completion ??= Task.FromResult(SignInEnd.Cancelled);
            try { await _stop.CancelAsync(); }
            catch (ObjectDisposedException) { }
            catch (Exception) { SafeLog("signin_cancel_failed"); }
            await completion;
        }

        public async ValueTask DisposeAsync()
        {
            await CancelAsync();
            _stop.Dispose();
        }

        private async Task<SignInEnd> RunAsync()
        {
            var end = SignInEnd.Failed;
            try { end = await WorkAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { end = SignInEnd.Cancelled; }
            catch (JsonRpcException error) { end = error.Code == -32800 ? SignInEnd.Cancelled : SignInEnd.Refused; }
            catch (IOException)
            {
                end = _link?.Exited.IsCompleted == true ? SignInEnd.AdapterCrashed : SignInEnd.LinkLost;
                SafeLog("signin_link_lost");
            }
            catch (Exception)
            {
                end = _link?.Exited.IsCompleted == true ? SignInEnd.AdapterCrashed : SignInEnd.Failed;
                SafeLog("signin_failed");
            }
            finally
            {
                lock (_gate)
                {
                    _closing = true;
                    try { _runner._board.Clear(operationId); }
                    catch (Exception) { SafeLog("signin_challenge_cleanup_failed"); }
                }
                try { await _stop.CancelAsync(); }
                catch (Exception) { SafeLog("signin_cancel_failed"); }
                if (_terminal is { } terminal)
                {
                    try { await _runner._terminals.CloseAsync(terminal, CancellationToken.None); }
                    catch (Exception) { end = SignInEnd.Failed; SafeLog("signin_exit_failed"); }
                }
                if (_input is { } input)
                    await input.StoppedAsync();
                if (!await CloseLinkAsync()) end = SignInEnd.Failed;
            }
            return end;
        }

        private async Task<SignInEnd> WorkAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _link = await LaunchAsync(token);
            token.ThrowIfCancellationRequested();
            var initialized = await _link.Client.InitializeAsync(ClientInfo, _runner._launcher.CanRunTerminalAuthentication, token);
            var methods = SignInMethods.Runnable(initialized.AuthMethods);
            var method = selectedMethodId is null ? SignInMethods.Choose(methods) : methods.FirstOrDefault(m => m.MethodId == selectedMethodId);
            if (method is null) return SignInEnd.Refused;
            if (method.Kind == SignInKind.Agent)
            {
                // Presentation alone never posts or grants a competing URL generation.
                await _link.Client.LoginAsync(method.MethodId, _ => { },
                    (url, consentToken) => _runner._board.RequestUrlConsentAsync(operationId, url, consentToken), token);
                return SignInEnd.SignedIn;
            }
            if (!_runner._launcher.CanRunTerminalAuthentication) return SignInEnd.Refused;
            if (!await CloseLinkAsync()) return SignInEnd.Failed;
            var terminalEnvironment = environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
            foreach (var value in method.Env) terminalEnvironment[value.Name] = value.Value;
            var program = new ProgramStart(agent.Command, [.. agent.Args, .. method.Args], terminalEnvironment,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "signin:" + operationId);
            var terminal = await _runner._terminals.StartProgramAsync(program, token);
            _terminal = terminal.Id;
            _input = new TerminalInput(this, terminal.Id);
            await ReadTerminalAsync(terminal.Id, token);
            if (await _runner._terminals.ExitedAsync(terminal.Id, token) != 0) return SignInEnd.Refused;
            return await VerifyAsync(token);
        }

        private Task<IAgentLink> LaunchAsync(CancellationToken token) =>
            _runner._launcher.LaunchSignInAsync(agent, environment, new DenyPermission(), new StageTimes(_runner._time), token);

        private async Task<SignInEnd> VerifyAsync(CancellationToken token)
        {
            _link = await LaunchAsync(token);
            token.ThrowIfCancellationRequested();
            AuthStatus? status = null;
            void Changed(AuthStatus value) => Volatile.Write(ref status, value);
            _link.Client.AuthStatusChanged += Changed;
            InitializeResponse? initialized = null;
            NewSessionResponse? session = null;
            var authRequired = false;
            try
            {
                initialized = await _link.Client.InitializeAsync(ClientInfo, _runner._launcher.CanRunTerminalAuthentication, token);
                try { session = await _link.Client.NewSessionAsync(new(_runner._probeFolder, []), token); }
                catch (JsonRpcException error) when (error.Error.IsAuthRequired) { authRequired = true; }
                var facts = ProviderFactsClassifier.Classify(agent,
                    new(initialized, session, Volatile.Read(ref status), authRequired, null), _runner._time.GetUtcNow().ToUnixTimeMilliseconds());
                return initialized is not null && session is not null && !authRequired && facts.Installation == Installation.Installed
                    && facts.Auth is AuthState.Unverified or AuthState.SignedIn
                    && facts.Problem is null or ProviderProblem.version_unknown
                    ? SignInEnd.SignedIn : SignInEnd.Refused;
            }
            finally
            {
                _link.Client.AuthStatusChanged -= Changed;
                if (session is not null)
                {
                    using var close = new CancellationTokenSource(SharedAdapter.CloseLimit, _runner._time);
                    try { await _link.Client.CloseSessionAsync(session.SessionId, close.Token); }
                    catch (Exception) { SafeLog("signin_probe_close_failed"); }
                }
            }
        }

        private async Task ReadTerminalAsync(string terminal, CancellationToken token)
        {
            var pending = "";
            var decoder = Encoding.UTF8.GetDecoder();
            await foreach (var piece in _runner._terminals.OutputAsync(terminal, token))
            {
                if (_input!.Posted) continue;
                var characters = new char[Encoding.UTF8.GetMaxCharCount(piece.Length)];
                var count = decoder.GetChars(piece.Span, characters, false);
                pending += new string(characters, 0, count);
                if (PostLink(ref pending, false)) continue;
                var start = pending.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
                if (start < 0) pending = pending[^Math.Min(pending.Length, 7)..];
                else if (start > 0) pending = pending[start..];
            }
            if (!_input!.Posted) PostLink(ref pending, true);
        }

        private bool PostLink(ref string pending, bool ended)
        {
            var start = pending.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return false;
            var stop = start;
            while (stop < pending.Length && !char.IsWhiteSpace(pending[stop]) && pending[stop] != '\u001b') stop++;
            if (stop == pending.Length && !ended) return false;
            if (!Uri.TryCreate(pending[start..stop], UriKind.Absolute, out var url)) return false;
            lock (_gate)
            {
                if (_closing || _stop.IsCancellationRequested) return false;
                _input!.Post(url);
            }
            pending = "";
            return true;
        }

        private async Task<bool> CloseLinkAsync()
        {
            if (_link is not { } link) return true;
            try { await link.DisposeAsync(); return true; }
            catch (Exception) { SafeLog("signin_adapter_cleanup_failed"); return false; }
            finally { _link = null; }
        }

        private void SafeLog(string code)
        {
            try { _runner._log.WriteLine(code); }
            catch (Exception) { /* A failed diagnostic sink must not fault Completion. */ }
        }

        private sealed class TerminalInput(Session owner, string terminal) : IChallengeInput
        {
            private Uri? _url;
            private int _deliveries;
            private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Posted => _url is not null;
            public void Post(Uri url)
            {
                _url = url;
                var callback = SignInMethods.Callback(url);
                owner._runner._board.Post(owner.OperationId, url, callback?.Host, callback?.Port, true, terminal, this);
            }
            public async ValueTask SubmitAsync(string code, CancellationToken ct)
            {
                lock (owner._gate)
                {
                    if (owner._closing || owner._stop.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (_deliveries++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                try
                {
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, owner._stop.Token);
                    await owner._runner._terminals.WriteAsync(terminal, Encoding.UTF8.GetBytes(code + "\r"), stop.Token);
                    lock (owner._gate)
                        if (!owner._closing && !owner._stop.IsCancellationRequested) Post(_url!);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || owner._stop.IsCancellationRequested) { throw; }
                catch (Exception) { throw new Assembly.Application.Common.Calls.CallFailed("signin_write_failed", "signin_write_failed"); }
                finally
                {
                    lock (owner._gate)
                        if (--_deliveries == 0) _drained.TrySetResult();
                }
            }
            public Task StoppedAsync()
            {
                lock (owner._gate) return _deliveries == 0 ? Task.CompletedTask : _drained.Task;
            }
        }
    }
}
