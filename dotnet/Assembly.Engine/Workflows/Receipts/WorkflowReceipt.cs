using System.Text.Json.Serialization;

namespace Assembly.Engine.Workflows.Receipts;

// The immutable workflow definition supplies the union tag; a body cannot select another result kind.
public abstract record WorkflowReceipt;

public sealed record BuildReceipt([property: JsonRequired] IReadOnlyList<string> Commits,
    [property: JsonRequired] IReadOnlyList<string> TestsAdded, [property: JsonRequired] string Summary) : WorkflowReceipt;

public sealed record ReconReceipt([property: JsonRequired] string Summary, IReadOnlyList<string> Existing,
    string? NoneFoundBecause, [property: JsonRequired] IReadOnlyList<string> Touches) : WorkflowReceipt
{
    [JsonConstructor]
    public ReconReceipt(string summary, IReadOnlyList<string> touches, IReadOnlyList<string> existing = null!, string? noneFoundBecause = null)
        : this(summary, existing ?? [], noneFoundBecause, touches) { }
}

public sealed record AcceptanceItem([property: JsonRequired] string Id, [property: JsonRequired] string Text,
    bool Edge, string? Fixture, bool? Restart, [property: JsonRequired] string ProvedBy);

public sealed record SpecReceipt([property: JsonRequired] string Url, [property: JsonRequired] string Summary,
    [property: JsonRequired] IReadOnlyList<string> Requirements, [property: JsonRequired] IReadOnlyList<AcceptanceItem> Acceptance,
    [property: JsonRequired] IReadOnlyList<string> Reuse, [property: JsonRequired] string Budget,
    [property: JsonRequired] IReadOnlyList<string> Assumptions) : WorkflowReceipt;

public sealed record PlanSlice([property: JsonRequired] uint Number, [property: JsonRequired] string Title,
    [property: JsonRequired] IReadOnlyList<uint> Needs, [property: JsonRequired] string Budget,
    [property: JsonRequired] IReadOnlyList<string> Files, [property: JsonRequired] IReadOnlyList<AcceptanceItem> Acceptance);

public sealed record PlanReceipt([property: JsonRequired] string Url,
    [property: JsonRequired] IReadOnlyList<PlanSlice> Slices) : WorkflowReceipt;

public sealed record EarlierGrade([property: JsonRequired] string Text, [property: JsonRequired] string Grade);
public sealed record ReviewFinding([property: JsonRequired] string Severity, [property: JsonRequired] bool Late,
    string? WhyLate, [property: JsonRequired] string PathLine, [property: JsonRequired] string Text,
    [property: JsonRequired] string Fix, IReadOnlyList<EarlierGrade> GradesOfEarlier)
{
    [JsonConstructor]
    public ReviewFinding(string severity, bool late, string pathLine, string text, string fix,
        IReadOnlyList<EarlierGrade> gradesOfEarlier = null!, string? whyLate = null)
        : this(severity, late, whyLate, pathLine, text, fix, gradesOfEarlier ?? []) { }
}
public sealed record NamedVerdict([property: JsonRequired] string Name, [property: JsonRequired] string Verdict);
public sealed record ReviewReceipt([property: JsonRequired] string Verdict, [property: JsonRequired] IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<string> Accepted, IReadOnlyList<NamedVerdict> NewNames, [property: JsonRequired] string Head) : WorkflowReceipt
{
    [JsonConstructor]
    public ReviewReceipt(string verdict, IReadOnlyList<ReviewFinding> findings, string head,
        IReadOnlyList<string> accepted = null!, IReadOnlyList<NamedVerdict> newNames = null!)
        : this(verdict, findings, accepted ?? [], newNames ?? [], head) { }
}

public sealed record ProofCheck([property: JsonRequired] string Name, [property: JsonRequired] string Result,
    [property: JsonRequired, JsonPropertyName("receiptUrl")] string ReceiptUrl);
public sealed record ProofReceipt([property: JsonRequired] string Summary, [property: JsonRequired] IReadOnlyList<string> Files,
    [property: JsonRequired, JsonPropertyName("proofUrls")] IReadOnlyList<string> ProofUrls,
    [property: JsonRequired] string Head, [property: JsonRequired] IReadOnlyList<ProofCheck> Checks) : WorkflowReceipt;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(BuildReceipt))]
[JsonSerializable(typeof(ReconReceipt))]
[JsonSerializable(typeof(SpecReceipt))]
[JsonSerializable(typeof(PlanReceipt))]
[JsonSerializable(typeof(ReviewReceipt))]
[JsonSerializable(typeof(ProofReceipt))]
public partial class ReceiptJson : JsonSerializerContext;
