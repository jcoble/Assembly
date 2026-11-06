using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Draft.Base.Chat;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Files;

/// <summary>The real files feature with its handlers in reach, for what a socket can't show: another peer, and the
/// order key on its own. Call arguments are JSON, as on the wire.</summary>
internal sealed class FilesRig : ICallRegistry, IAsyncDisposable
{
    private readonly TempDatabase _file = new();
    private readonly DirectoryInfo _checkouts = Directory.CreateTempSubdirectory("files-rig-");
    private readonly AppDatabase _db;
    private readonly BackgroundWork _work = new(TimeProvider.System, TextWriter.Null);
    private readonly Dictionary<string, Func<string, CallContext, Task<JsonElement>>> _calls = [];
    private readonly Dictionary<string, Func<string, string?>> _orders = [];

    internal FileOwner Owners { get; }

    internal FilesRig()
    {
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, new TopicSet());
        var sessions = SessionStore.Open(_db, TimeProvider.System, _checkouts.FullName);
        global::Assembly.Engine.Persistence.EngineSchema.Open(_db, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Owners = new FileOwner(sessions, _db);
        new FilesFeature(sessions, Owners, _work).Register(this);
    }

    internal Task<JsonElement> CallAsync(string method, string args, CallPeer? peer = null) =>
        _calls[method](args, new CallContext(default, "c", Peer: peer ?? new OwnerPeer()));

    internal string? Order(string method, string args) => _orders[method](args);

    /// <summary>A save that has its bytes and awaits its commit.</summary>
    internal async Task<string> StagedAsync(string path, string text)
    {
        var begun = await CallAsync("beginSave", $$"""{"target":{"path":"{{path}}"},"expectedRevision":"{{await FileSave.RevisionAsync(path, default)}}","size":{{text.Length}}}""");
        var upload = begun.GetProperty("upload").GetString()!;
        await CallAsync("saveChunk", $$"""{"upload":"{{upload}}","offset":0,"data":"{{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))}}"}""");
        return upload;
    }

    public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) =>
        _calls[method] = async (json, call) =>
            JsonSerializer.SerializeToElement(await handler(JsonSerializer.Deserialize(json, args)!, call), result);

    public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
        Action<TArgs, CallContext>? failed = null, bool bulk = false)
    {
        _calls[method] = async (json, call) =>
            JsonSerializer.SerializeToElement((await handler(JsonSerializer.Deserialize(json, args)!, call))(null!), result);
        _orders[method] = json => order?.Invoke(JsonSerializer.Deserialize(json, args)!);
    }

    public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) { }
    public void Closed(Action<string> connection) { }
    public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
        Func<string?, CallContext, IReadOnlyList<TItem>> state, bool terminal = false)
    { }
    public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
        Action<string, CallContext>? opened = null)
    { }

    public async ValueTask DisposeAsync()
    {
        await _work.DisposeAsync();
        _db.Dispose();
        _file.Dispose();
        _checkouts.Delete(recursive: true);
    }
}
