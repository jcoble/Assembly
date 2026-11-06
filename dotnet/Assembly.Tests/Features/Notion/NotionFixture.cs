using System.Net;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Security;
using Assembly.Application.Features.Notion;
using Assembly.Infrastructure.Notion;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Notion;

public sealed class NotionFixture : IAsyncDisposable
{
    public const string Source = "11111111-1111-1111-1111-111111111111";
    public const string Other = "22222222-2222-2222-2222-222222222222";
    public const string Page = "33333333-3333-3333-3333-333333333333";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notion-test-" + Guid.NewGuid());
    public AppDatabase Database { get; }
    public FakeSecrets Secrets { get; } = new();
    public FakeHttp Handler { get; } = new();
    public HttpClient Client { get; }
    public NotionSettings Settings { get; }
    private NotionCalls? _calls;
    private CaptureRegistry? _registry;
    public CallPeer? Peer { get; set; } = new OwnerPeer();
    public NotionOAuth OAuth { get; }
    public NotionHttp Http { get; }
    public NotionFixture()
    {
        Directory.CreateDirectory(_root);
        Database = AppDatabase.Open(Path.Combine(_root, "test.db"), TimeProvider.System, new Topics());
        Client = new HttpClient(Handler);
        Http = new NotionHttp(Client);
        Settings = new NotionSettings(Database, Secrets, Http, TimeProvider.System);
        Http.Bind(Settings);
        OAuth = new NotionOAuth(Client, Settings, TimeProvider.System);
    }
    public ResponseGate Gate(string route) { var gate = new ResponseGate(); Handler.Gates[route] = gate; return gate; }
    public sealed class ResponseGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public void Enqueue(string route, string json, int status = 200) => Handler.Queue.Enqueue((route, json, status));
    public void FailNextSqlCommit() => Database.Writer.Write("test.fail", s => s.Execute("""
        CREATE TRIGGER fail_save BEFORE UPDATE ON notion_connection WHEN NEW.state='connected'
        BEGIN SELECT RAISE(ABORT,'injected commit failure'); END;
        """));
    public async Task<string?> ActiveSecretText()
    {
        var a = Database.QueryFirst("SELECT secret_account FROM notion_connection WHERE singleton=1", r => r.IsNull(0) ? null : r.Text(0), default);
        return a is null ? null : Encoding.UTF8.GetString((await Secrets.GetAsync(NotionSettings.Service, a, default))!);
    }
    public Task<JsonElement> RequestAsync(string method, object args)
    {
        if (_calls is null)
        {
            var cache = new NotionCache(Database, Settings);
            var edits = new NotionEdits(Database, Settings, Http);
            _calls = new NotionCalls(Settings, OAuth, new NotionDetailReader(Http), cache, new NotionRefresh(Http, cache, Settings, TimeProvider.System), edits, new NotionImageUpload(Path.Combine(_root, "images"), Http, Settings, edits));
            _registry = new CaptureRegistry(Database);
            _calls.Register(_registry);
        }
        return _registry!.Handlers[method](JsonSerializer.SerializeToElement(args), new(default, "test-connection", Peer: Peer));
    }
    public async ValueTask DisposeAsync()
    {
        if (_calls is not null) await _calls.DisposeAsync();
        await OAuth.DisposeAsync();
        Client.Dispose();
        Database.Dispose();
        Directory.Delete(_root, true);
    }
    public sealed class FakeSecrets : ISecretStore
    {
        public Dictionary<string, byte[]> Items { get; } = [];
        public bool Unavailable { get; set; }
        public bool FailDelete { get; set; }
        public Task<byte[]?> GetAsync(string service, string account, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Unavailable) throw new NotionException("keychain_unavailable");
            return Task.FromResult(Items.TryGetValue(account, out var b) ? b.ToArray() : null);
        }
        public Task PutAsync(string service, string account, ReadOnlyMemory<byte> value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Unavailable) throw new NotionException("keychain_unavailable");
            Items[account] = value.ToArray();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string service, string account, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Unavailable || FailDelete) throw new NotionException("keychain_unavailable");
            Items.Remove(account);
            return Task.CompletedTask;
        }
    }
    public sealed class FakeHttp : HttpMessageHandler
    {
        public Queue<(string Route, string Json, int Status)> Queue { get; } = new();
        public System.Collections.Concurrent.ConcurrentDictionary<string, ResponseGate> Gates { get; } = new();
        public bool LoseNextPatch { get; set; }
        public List<(string Method, string Path, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (LoseNextPatch && request.Method == HttpMethod.Patch) { LoseNextPatch = false; throw new HttpRequestException("fake response lost"); }
            if (Gates.TryRemove(request.RequestUri.PathAndQuery, out var gate)) { gate.Started.TrySetResult(); await gate.Release.Task.WaitAsync(ct); }
            Assert.True(Queue.Count > 0, "Unexpected HTTP request (credentials redacted)");
            var next = Queue.Dequeue();
            Assert.Equal(next.Route, request.RequestUri.PathAndQuery);
            return new HttpResponseMessage((HttpStatusCode)next.Status) { Content = new StringContent(next.Json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CaptureRegistry(IAppDatabase db) : ICallRegistry
    {
        public Dictionary<string, Func<JsonElement, CallContext, Task<JsonElement>>> Handlers { get; } = [];
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result, Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) =>
            Handlers[method] = async (value, call) => JsonSerializer.SerializeToElement(await handler(value.Deserialize(args)!, call), result);
        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result, Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null, Action<TArgs, CallContext>? failed = null, bool bulk = false) =>
            Handlers[method] = async (value, call) =>
            {
                var input = value.Deserialize(args)!;
                try { return JsonSerializer.SerializeToElement(db.Writer.Write(feature + "/" + method, await handler(input, call)), result); }
                catch { failed?.Invoke(input, call); throw; }
            };
        public void Topic<T>(string topic, JsonTypeInfo<T> item, Func<T, string> key, Func<string?, CallContext, IReadOnlyList<T>> state, bool terminal = false) { }
        public void Closed(Action<string> closed) { }
        public void Save<T>(string feature, JsonTypeInfo<T> item, Action<string, T, IWriteScope> save) => throw new NotSupportedException();
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => throw new NotSupportedException();
    }
}
