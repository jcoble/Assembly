using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>The branch, upstream counts, base branch and age when off the base, and at most 1000 changed files, with the total before that limit.</summary>
public sealed record GitStatus(string? Branch, string? Head, int Ahead, int Behind,
    bool HasUpstream, IReadOnlyList<ChangedFile> Files, int TotalFiles, string Fingerprint = "")
{
    public int? TotalStaged { get; init; }
    public string? CheckoutRoot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseBranch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BranchCreatedAt { get; init; }
}
