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
internal sealed class FilesFeature(SessionStore sessions) : IFeature
{
    public const string Feature = "files";
    private readonly FileReadSession reads = new();
    private readonly FileSaveUploads uploads = new();

    public void Register(ICallRegistry calls)
    {
        calls.Read(Feature, "list", FilesJson.Default.ListArgs, FilesJson.Default.FileListing, (args, call) =>
            DirectoryListing.ListAsync(SessionPaths.RootOf(sessions, args.Session), args.Dir, args.IncludeExcluded, call.Cancellation));
        calls.Read(Feature, "search", FilesJson.Default.SearchArgs, FilesJson.Default.SearchPage, (args, call) =>
            NameSearch.PageAsync(SessionPaths.RootOf(sessions, args.Session), args.Query, args.Cursor, args.PageSize, call.Cancellation));
        calls.Read(Feature, "read", FilesJson.Default.ReadArgs, FilesJson.Default.FileChunk, (args, call) =>
            reads.ReadAsync(SessionPaths.RootOf(sessions, args.Session), args.Path, args.Offset, args.Revision, call.Connection,
                call.Cancellation), bulk: true);
        calls.Read(Feature, "release", FilesJson.Default.ReleaseArgs, FilesJson.Default.ReleasedRead, (args, call) =>
            Task.FromResult(new ReleasedRead(reads.Release(SessionPaths.RootOf(sessions, args.Session), args.Path,
                args.Revision, call.Connection))), bulk: true);
        calls.Write(Feature, "beginSave", FilesJson.Default.BeginSave, FilesJson.Default.BegunSave, async (args, call) =>
        {
            call.Closed.ThrowIfCancellationRequested();
            var result = await uploads.BeginAsync(SessionPaths.RootOf(sessions, args.Session), args, call.Connection, call.Cancellation);
            if (call.Closed.IsCancellationRequested) { uploads.Drop(result.Upload, call.Connection); call.Closed.ThrowIfCancellationRequested(); }
            return _ => result;
        }, bulk: true);
        calls.Write(Feature, "saveChunk", FilesJson.Default.SaveChunk, FilesJson.Default.UploadAccepted, async (args, call) =>
        {
            call.Closed.ThrowIfCancellationRequested();
            await uploads.AppendAsync(args, call.Connection, call.Cancellation);
            return _ => new UploadAccepted(true);
        }, failed: (args, call) => uploads.Drop(args.Upload, call.Connection), bulk: true);
        calls.Write(Feature, "commitSave", FilesJson.Default.CommitSave, FilesJson.Default.SaveResult, async (args, call) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.Closed);
            var result = await uploads.CommitAsync(args.Upload, call.Connection, linked.Token);
            return _ => result;
        }, failed: (args, call) => uploads.Drop(args.Upload, call.Connection), bulk: true);
        calls.Write(Feature, "dropSave", FilesJson.Default.DropSave, FilesJson.Default.UploadAccepted, (args, call) =>
        {
            uploads.Drop(args.Upload, call.Connection);
            return Task.FromResult<Func<Assembly.Application.Common.Database.IWriteScope, UploadAccepted>>(_ => new UploadAccepted(true));
        }, bulk: true);
        calls.Closed(uploads.Close);
        calls.Closed(reads.Close);
    }

    internal sealed record ListArgs([property: JsonRequired] string Session, string Dir = "", bool IncludeExcluded = false);

    internal sealed record SearchArgs([property: JsonRequired] string Session, [property: JsonRequired] string Query, int Cursor = 0,
        int PageSize = 100);

    internal sealed record ReadArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path, long Offset = 0,
        string? Revision = null);

    internal sealed record ReleaseArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path,
        [property: JsonRequired] string Revision);
    internal sealed record ReleasedRead(bool Released);
    internal sealed record UploadAccepted(bool Accepted);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FilesFeature.ListArgs))]
[JsonSerializable(typeof(FilesFeature.SearchArgs))]
[JsonSerializable(typeof(FilesFeature.ReadArgs))]
[JsonSerializable(typeof(FilesFeature.ReleaseArgs))]
[JsonSerializable(typeof(FilesFeature.ReleasedRead))]
[JsonSerializable(typeof(BeginSave))]
[JsonSerializable(typeof(BegunSave))]
[JsonSerializable(typeof(SaveChunk))]
[JsonSerializable(typeof(CommitSave))]
[JsonSerializable(typeof(DropSave))]
[JsonSerializable(typeof(SaveResult))]
[JsonSerializable(typeof(FilesFeature.UploadAccepted))]
[JsonSerializable(typeof(FileChunk))]
[JsonSerializable(typeof(FileListing))]
[JsonSerializable(typeof(SearchPage))]
internal sealed partial class FilesJson : JsonSerializerContext;
