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

    public void Register(ICallRegistry calls)
    {
        calls.Read(Feature, "list", FilesJson.Default.ListArgs, FilesJson.Default.FileListing, (args, call) =>
            DirectoryListing.ListAsync(RootOf(args.Session), args.Dir, args.IncludeExcluded, call.Cancellation));
        calls.Read(Feature, "search", FilesJson.Default.SearchArgs, FilesJson.Default.SearchPage, (args, call) =>
            NameSearch.PageAsync(RootOf(args.Session), args.Query, args.Cursor, args.PageSize, call.Cancellation));
        calls.Read(Feature, "read", FilesJson.Default.ReadArgs, FilesJson.Default.FileChunk, (args, call) =>
            FileRead.ReadAsync(RootOf(args.Session), args.Path, args.Offset, call.Cancellation));
    }

    private string RootOf(string sessionId)
    {
        var session = sessions.Get(sessionId) ?? throw new CallFailed("unknown session");
        if (session.Machine is not null)
            throw new CallFailed($"this session's files are on {session.Machine}, which this window can't show yet");
        var root = sessions.Root(session) ?? throw new CallFailed("this session has no folder");
        return Directory.Exists(root) ? root : throw new CallFailed("this session's folder is made on its first message");
    }

    internal sealed record ListArgs([property: JsonRequired] string Session, string Dir = "", bool IncludeExcluded = false);

    internal sealed record SearchArgs([property: JsonRequired] string Session, [property: JsonRequired] string Query, int Cursor = 0,
        int PageSize = 100);

    internal sealed record ReadArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path, long Offset = 0);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FilesFeature.ListArgs))]
[JsonSerializable(typeof(FilesFeature.SearchArgs))]
[JsonSerializable(typeof(FilesFeature.ReadArgs))]
[JsonSerializable(typeof(FileChunk))]
[JsonSerializable(typeof(FileListing))]
[JsonSerializable(typeof(SearchPage))]
internal sealed partial class FilesJson : JsonSerializerContext;
