using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Infrastructure.Chat.Files;

public sealed record FileFault(string Code, string Message);
public sealed record SaveResult(string State, string? Revision, FileFault? Error);
public sealed record BeginSave(string Session, string Path, string ExpectedRevision, long Size, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
public sealed record BegunSave(string Upload);
public sealed record SaveChunk(string Upload, long Offset, byte[] Data, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
public sealed record CommitSave(string Upload, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
public sealed record DropSave(string Upload, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
public sealed record CompareFile(string Upload, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
public sealed record FileComparison(string? DiskRevision, GitFileDiff Diff);

public sealed record PageCursor(string Generation, string After);

public sealed record SearchArgs(string Session, string Query, string Mode = "name", bool IncludeExcluded = false,
    string? SearchId = null, int PageSize = 100, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
public sealed record ContentMatch(string Path, int Line, int Column, string Excerpt);
public sealed record SearchResult(string SearchId, IReadOnlyList<NameMatch> Names, IReadOnlyList<ContentMatch> Content,
    bool More, int Skipped, bool Truncated);
public sealed record ReleaseSearch(string SearchId, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);

public sealed record FileActionArgs(string Session, string Kind, string Path, string? Destination = null, string? Revision = null, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
public sealed record ActionResult(string State, string? Path, FileFault? Error);
