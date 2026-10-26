namespace Assembly.Domain.Rows;

public sealed record PlanPayload(IReadOnlyList<PlanItem> Items);
