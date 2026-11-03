using Assembly.Engine.Contracts;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Prs;

public abstract record PrCommand : EngineCommand;
public sealed record ReportPr(string? RunId, string Repo, int Number, string Head,
    string Url, string ReportedState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerSessionId = null) : PrCommand;
public sealed record ObservePr(string State, string Head, long ObservedMs) : PrCommand;

public sealed record PrObservationFacts(string SessionId, string? RunId, string? StepId,
    long SessionEpoch, string Repo, int Number, string Head, string Url, string State, long ObservedMs);
public sealed record RecordPrObservation(string CoordinatorMachine, PrObservationFacts Facts) : PrCommand;
public sealed record ApplyPrObservation(string OriginMachine, string ObservationId, PrObservationFacts Facts) : PrCommand;
public sealed record PrObservationRecorded(string ObservationId, string CoordinatorMachine, long CoordinatorSeq) : PrCommand;
public sealed record PrReportOutcome(string ObservationId, string State);
