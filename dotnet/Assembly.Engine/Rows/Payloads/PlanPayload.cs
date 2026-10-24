namespace Assembly.Engine.Rows.Payloads;

public sealed record PlanPayload(IReadOnlyList<PlanItem> Items);
