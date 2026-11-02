using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.DevSeed;

public sealed record SeedRequest(int Version, string CanonicalWorktree, string ComputedHome, string ManifestPath, bool DryRun = false);
public sealed record SeedManifest(int Version, string OriginId, string Kind, IReadOnlyList<SeedSource> Sources);
public sealed record SeedSource
{
    public required string Format { get; init; }
    public required string Path { get; init; }
    public string? Sha256 { get; init; }
    public string? SessionKey { get; init; }
    public string Provider { get; init; } = "unknown";
    public string ProviderConfidence { get; init; } = "uncertain";
    public string? OriginalHost { get; init; }
    public IReadOnlyList<string> Provenance { get; init; } = [];
    public IReadOnlyList<string>? SessionKeys { get; init; }
    public int? RowFormat { get; init; }
    public SeedMetadata Metadata { get; init; } = new();
    public IReadOnlyList<string> DetailPaths { get; init; } = [];
    public IReadOnlyList<SeedAsset> Files { get; init; } = [];
    public IReadOnlyList<SeedAsset> Attachments { get; init; } = [];
    public string? AttachmentRoot { get; init; }
}
public sealed record SeedMetadata(string? Title = null, string? ParentSessionKey = null, long CreatedAt = 0,
    long UpdatedAt = 0, string? SessionCost = null, long ReadSeq = 0, string? OriginalRepo = null, string? OriginalResumeHandle = null, string? OriginalProvider = null, IReadOnlyList<string>? ProviderConflicts = null);
public sealed record SeedAsset(string SourcePath, string RelativePath, string Sha256, string? SessionKey = null, string? OriginalPath = null);
public sealed record SeedRow(long Seq, string? RunId, string? ItemId, string Kind, JsonElement Payload, long CreatedAt);
public sealed record SeedInput(string Path, string Sha256);
public sealed record SeedSession(string Key, string Provider, string ProviderConfidence, string? OriginalHost,
    SeedMetadata Metadata, IReadOnlyList<string> Provenance, IReadOnlyList<SeedRow> Rows, IReadOnlyList<ToolDetail> Details,
    IReadOnlyList<SeedAsset> Files, IReadOnlyList<SeedAsset> Attachments, IReadOnlyList<SeedInput>? Inputs = null);
public sealed record SeedBundle(SeedManifest Manifest, IReadOnlyList<SeedSession> Sessions, IReadOnlyList<SeedInput> Inputs,
    int DeduplicatedRows, int SkippedRemote, IReadOnlyList<string> Limitations);
public sealed record SeedReport(int InputArtifacts, int UniqueSessions, int Rows, int Tools, int Details, int Files, int Attachments,
    int InsertedSessions, int InsertedRows, int InsertedDetails, int InsertedFiles, int DeduplicatedRows, int SkippedRemote,
    int RealSessions, int SyntheticSessions, IReadOnlyList<string> Providers, IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Limitations, string? ReceiptPath, bool DryRun, int GeneratedFiles = 0);
public static class SeedJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Stringify<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Expected non-null JSON.");
}
