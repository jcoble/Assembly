using Assembly.Engine.Contracts;

namespace Assembly.Engine.Prs;

public abstract record PrCommand : EngineCommand;
public sealed record ReportPr(string RunId, string Repo, int Number, string Head,
    string Url, string ReportedState) : PrCommand;
public sealed record ObservePr(string State, string Head, long ObservedMs) : PrCommand;
