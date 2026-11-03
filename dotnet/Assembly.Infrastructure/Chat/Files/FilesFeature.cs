using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// A session's folder over the socket: <c>files/list</c> answers one folder of it, <c>files/search</c> a page of
/// the files and folders whose name holds a query, and <c>files/read</c> a part of one file's text. Paths are from the session's folder (its <c>root</c> on the sessions
/// topic); nothing outside it is read. All are reads, so a cancel, the 30 s limit or the socket closing stops them.
/// A session on another machine has no folder here yet.
/// </summary>
internal sealed class FilesFeature(SessionStore sessions, FileOwner owners) : IFeature
{
    public const string Feature = "files";
    private readonly FileReadSession reads = new();
    private readonly FileSaveUploads uploads = new();
    private readonly FileObservations observations = new();
    private readonly SearchRun searches = new();

    public void Register(ICallRegistry calls)
    {
        calls.Read(Feature, "owner", FilesJson.Default.ObserveRoot, FilesJson.Default.FileOwnership, (args, call) =>
            Task.FromResult(owners.Resolve(args.Session, call.Cancellation)));
        calls.Read(Feature, "observe", FilesJson.Default.ObserveRoot, FilesJson.Default.ObservedRoot, (args, call) =>
            Task.FromResult(observations.Observe(LocalRoot(args.Session, call.Cancellation), call.Connection, call.Cancellation)));
        calls.Read(Feature, "unobserve", FilesJson.Default.UnobserveRoot, FilesJson.Default.UnobservedRoot, (args, call) =>
            Task.FromResult(new UnobservedRoot(observations.Release(args.Lease, call.Connection))));
        calls.Closed(observations.Close);
        calls.Read(Feature, "list", FilesJson.Default.ListArgs, FilesJson.Default.FileListing, (args, call) =>
            DirectoryListing.ListAsync(LocalRoot(args.Session, call.Cancellation), args.Dir, args.IncludeExcluded, call.Cancellation, args.Descending, args.Cursor));
        calls.Read(Feature, "search", FilesJson.Default.SearchArgs, FilesJson.Default.SearchResult, (args, call) =>
            searches.PageAsync(LocalRoot(args.Session, call.Cancellation), args, call.Connection, call.Cancellation));
        calls.Read(Feature, "releaseSearch", FilesJson.Default.ReleaseSearch, FilesJson.Default.UnobservedRoot, (args, call) =>
        {
            searches.Release(args.SearchId, call.Connection);
            return Task.FromResult(new UnobservedRoot(true));
        });
        calls.Closed(searches.Close);
        calls.Read(Feature, "read", FilesJson.Default.ReadArgs, FilesJson.Default.FileChunk, (args, call) =>
            reads.ReadAsync(LocalRoot(args.Session, call.Cancellation), args.Path, args.Offset, args.Revision, call.Connection,
                call.Cancellation, args.Session), bulk: true);
        calls.Read(Feature, "release", FilesJson.Default.ReleaseArgs, FilesJson.Default.ReleasedRead, (args, call) =>
            Task.FromResult(new ReleasedRead(reads.ReleaseSession(args.Session, args.Path, args.Revision, call.Connection))), bulk: true);
        calls.Write(Feature, "beginSave", FilesJson.Default.BeginSave, FilesJson.Default.BegunSave, async (args, call) =>
        {
            call.Closed.ThrowIfCancellationRequested();
            var result = await uploads.BeginAsync(LocalRoot(args.Session, call.Cancellation), args, call.Connection, call.Cancellation, call.ReplyId);
            if (call.Closed.IsCancellationRequested) { uploads.Drop(result.Upload, call.Connection); call.Closed.ThrowIfCancellationRequested(); }
            return _ => result;
        }, order: args => "files:" + SessionPaths.ResolveFile(LocalRoot(args.Session, CancellationToken.None), args.Path),
            failed: (_, call) => uploads.FailedBegin(call.Connection, call.ReplyId), bulk: true);
        calls.Write(Feature, "saveChunk", FilesJson.Default.SaveChunk, FilesJson.Default.UploadAccepted, async (args, call) =>
        {
            call.Closed.ThrowIfCancellationRequested();
            await uploads.AppendAsync(args, call.Connection, call.Cancellation);
            return _ => new UploadAccepted(true);
        }, order: args => uploads.OrderKey(args.Upload), failed: (args, call) => uploads.Drop(args.Upload, call.Connection), bulk: true);
        calls.Write(Feature, "commitSave", FilesJson.Default.CommitSave, FilesJson.Default.SaveResult, async (args, call) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.Closed);
            var result = await uploads.CommitAsync(args.Upload, call.Connection, linked.Token, (session, root) =>
            {
                if (LocalRoot(session, linked.Token) != root) throw new CallFailed("the session's checkout changed; the draft is preserved");
            });
            return _ => result;
        }, order: args => uploads.OrderKey(args.Upload), failed: (args, call) => uploads.Drop(args.Upload, call.Connection), bulk: true);
        calls.Read(Feature, "compare", FilesJson.Default.CompareFile, FilesJson.Default.FileComparison, async (args, call) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.Closed);
            return await uploads.CompareAsync(args.Upload, call.Connection, linked.Token, (session, root) =>
            {
                if (LocalRoot(session, linked.Token) != root) throw new CallFailed("the session's checkout changed; the draft is preserved");
            });
        }, bulk: true);
        calls.Write(Feature, "dropSave", FilesJson.Default.DropSave, FilesJson.Default.UploadAccepted, (args, call) =>
        {
            uploads.Drop(args.Upload, call.Connection);
            return Task.FromResult<Func<Assembly.Application.Common.Database.IWriteScope, UploadAccepted>>(_ => new UploadAccepted(true));
        }, bulk: true);
        calls.Write(Feature, "action", FilesJson.Default.FileActionArgs, FilesJson.Default.ActionResult, async (args, call) =>
        {
            var result = await FileActions.ExecuteAsync(LocalRoot(args.Session, call.Cancellation), args, call.Cancellation);
            return _ => result;
        }, order: args => "files:" + SessionPaths.ResolveFile(LocalRoot(args.Session, CancellationToken.None), args.Path));
        calls.Closed(uploads.Close);
        calls.Closed(reads.Close);
    }

    private string LocalRoot(string session, CancellationToken ct)
    {
        var owner = owners.Resolve(session, ct);
        if (!owners.IsLocal(owner, ct)) throw new CallFailed("this session's owning machine must perform the file operation");
        // Preserve the existing session path contract while refusing a stale or foreign ownership projection.
        if (SessionPaths.RootOf(sessions, session) != owner.CheckoutRoot) throw new CallFailed("the session's checkout changed; reopen it");
        return owner.CheckoutRoot;
    }

    internal sealed record ListArgs([property: JsonRequired] string Session, string Dir = "", bool IncludeExcluded = false, bool Descending = false, PageCursor? Cursor = null);

    internal sealed record ReadArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path, long Offset = 0,
        string? Revision = null);

    internal sealed record ReleaseArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path,
        [property: JsonRequired] string Revision);
    internal sealed record ReleasedRead(bool Released);
    internal sealed record UploadAccepted(bool Accepted);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FilesFeature.ListArgs))]
[JsonSerializable(typeof(SearchArgs))]
[JsonSerializable(typeof(ReleaseSearch))]
[JsonSerializable(typeof(FilesFeature.ReadArgs))]
[JsonSerializable(typeof(FilesFeature.ReleaseArgs))]
[JsonSerializable(typeof(FilesFeature.ReleasedRead))]
[JsonSerializable(typeof(BeginSave))]
[JsonSerializable(typeof(BegunSave))]
[JsonSerializable(typeof(SaveChunk))]
[JsonSerializable(typeof(CommitSave))]
[JsonSerializable(typeof(DropSave))]
[JsonSerializable(typeof(CompareFile))]
[JsonSerializable(typeof(FileComparison))]
[JsonSerializable(typeof(SaveResult))]
[JsonSerializable(typeof(FilesFeature.UploadAccepted))]
[JsonSerializable(typeof(FileChunk))]
[JsonSerializable(typeof(FileActionArgs))]
[JsonSerializable(typeof(ActionResult))]
[JsonSerializable(typeof(FileOwnership))]
[JsonSerializable(typeof(ObserveRoot))]
[JsonSerializable(typeof(UnobserveRoot))]
[JsonSerializable(typeof(ObservedRoot))]
[JsonSerializable(typeof(UnobservedRoot))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(FileListing))]
[JsonSerializable(typeof(NameMatch))]
[JsonSerializable(typeof(ContentMatch))]
[JsonSerializable(typeof(SearchResult))]
internal sealed partial class FilesJson : JsonSerializerContext;
