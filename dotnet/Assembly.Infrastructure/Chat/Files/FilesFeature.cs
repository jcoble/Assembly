using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// A session's folder over the socket: <c>files/list</c> answers one folder of it and <c>files/search</c> a page of
/// the files and folders whose name holds a query. Paths are from the session's folder (its <c>root</c> on the sessions
/// topic); nothing outside it is read. Both are reads, so a cancel, the 30 s limit or the socket closing stops them.
/// A session on another machine has no folder here yet.
/// </summary>
internal sealed class FilesFeature(SessionStore sessions) : IFeature
{
    public const string Feature = "files";

    public void Register(ICallRegistry calls)
    {
        calls.Read(Feature, "list", FilesJson.Default.ListArgs, FilesJson.Default.FileListing, (args, call) =>
            DirectoryListing.ListAsync(SessionPaths.RootOf(sessions, args.Session), args.Dir, args.IncludeExcluded, call.Cancellation));
        calls.Read(Feature, "search", FilesJson.Default.SearchArgs, FilesJson.Default.SearchPage, (args, call) =>
            NameSearch.PageAsync(SessionPaths.RootOf(sessions, args.Session), args.Query, args.Cursor, args.PageSize, call.Cancellation));
    }

    internal sealed record ListArgs([property: JsonRequired] string Session, string Dir = "", bool IncludeExcluded = false);

    internal sealed record SearchArgs([property: JsonRequired] string Session, [property: JsonRequired] string Query, int Cursor = 0,
        int PageSize = 100);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FilesFeature.ListArgs))]
[JsonSerializable(typeof(FilesFeature.SearchArgs))]
[JsonSerializable(typeof(FileListing))]
[JsonSerializable(typeof(SearchPage))]
internal sealed partial class FilesJson : JsonSerializerContext;
