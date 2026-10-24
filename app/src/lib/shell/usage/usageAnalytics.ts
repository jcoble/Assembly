import type {
  UsageDailyTotalsRow,
  UsageHistoryQuery,
  UsageRange,
  UsageSummary,
  UsageTokenBreakdown
} from './usageTypes.ts';

export const usageRangeOptions: ReadonlyArray<{ value: UsageRange; label: string }> = [
  { value: 'today', label: 'Today' },
  { value: 'yesterday', label: 'Yesterday' },
  { value: '30-days', label: '30 Days' }
];

export type UsageHeatmapDay = {
  day: string;
  totalTokens: number;
  intensity: 0 | 1 | 2 | 3 | 4;
};

export type UsageTrendPoint = {
  day: string;
  totalTokens: number;
  x: number;
  y: number;
};

export function usageSummaryLabel(summary: Pick<UsageSummary, 'inputTokens' | 'outputTokens'>): string {
  const total = summary.inputTokens + summary.outputTokens;
  return `${total.toLocaleString('en-US')} tokens`;
}

export function usageRangeQuery(range: UsageRange, now: Date = new Date()): UsageHistoryQuery {
  if (range === 'all') return {};
  const today = new Date(now);
  today.setHours(0, 0, 0, 0);
  const tomorrow = new Date(today);
  tomorrow.setDate(tomorrow.getDate() + 1);
  if (range === 'today') {
    return { startMicros: today.getTime() * 1_000, endMicros: tomorrow.getTime() * 1_000 };
  }
  if (range === 'yesterday') {
    const yesterday = new Date(today);
    yesterday.setDate(yesterday.getDate() - 1);
    return { startMicros: yesterday.getTime() * 1_000, endMicros: today.getTime() * 1_000 };
  }
  const thirtyDaysAgo = new Date(today);
  thirtyDaysAgo.setDate(thirtyDaysAgo.getDate() - 29);
  return { startMicros: thirtyDaysAgo.getTime() * 1_000, endMicros: tomorrow.getTime() * 1_000 };
}

export function usageHeatmapQuery(range: UsageRange, now: Date = new Date()): UsageHistoryQuery {
  const today = new Date(now);
  today.setHours(0, 0, 0, 0);
  if (range === 'yesterday') today.setDate(today.getDate() - 1);
  const start = new Date(today);
  start.setDate(start.getDate() - 41);
  const end = new Date(today);
  end.setDate(end.getDate() + 1);
  return { startMicros: start.getTime() * 1_000, endMicros: end.getTime() * 1_000 };
}

export function usageRangeLabel(range: UsageRange): string {
  return usageRangeOptions.find((option) => option.value === range)?.label ?? 'Selected range';
}

export function usageTotalTokens(tokens: Pick<UsageTokenBreakdown, 'inputTokens' | 'outputTokens' | 'cacheReadTokens' | 'cacheWriteTokens'>): number {
  return tokens.inputTokens + tokens.outputTokens + tokens.cacheReadTokens + tokens.cacheWriteTokens;
}

export function usageCacheShare(tokens: Pick<UsageTokenBreakdown, 'cacheReadTokens' | 'cacheWriteTokens' | 'totalTokens'>): number {
  if (tokens.totalTokens === 0) return 0;
  return Math.round(((tokens.cacheReadTokens + tokens.cacheWriteTokens) / tokens.totalTokens) * 100);
}

export function buildUsageHeatmap(
  rows: readonly UsageDailyTotalsRow[],
  range: UsageRange,
  now: Date = new Date()
): UsageHeatmapDay[] {
  const dayCount = 42;
  const anchor = new Date(now);
  anchor.setHours(0, 0, 0, 0);
  if (range === 'yesterday') anchor.setDate(anchor.getDate() - 1);
  const indexed = new Map(rows.map((row) => [row.day, row]));
  const days: UsageHeatmapDay[] = [];
  for (let index = dayCount - 1; index >= 0; index -= 1) {
    const date = new Date(anchor);
    date.setDate(date.getDate() - index);
    const day = [
      date.getFullYear(),
      String(date.getMonth() + 1).padStart(2, '0'),
      String(date.getDate()).padStart(2, '0')
    ].join('-');
    const row = indexed.get(day);
    const totalTokens = row?.totalTokens ?? 0;
    const maximum = row?.rangeMaxTokens ?? rows[0]?.rangeMaxTokens ?? 0;
    const intensity = totalTokens === 0 || maximum === 0
      ? 0
      : Math.min(4, Math.max(1, Math.ceil((totalTokens / maximum) * 4)));
    days.push({ day, totalTokens, intensity: intensity as UsageHeatmapDay['intensity'] });
  }
  return days;
}

export function buildProviderUsageTrend(
  rows: readonly { day: string; provider: string; totalTokens: number }[],
  provider: string,
  now: Date = new Date(),
  dayCount = 30
): UsageTrendPoint[] {
  const providerRows = new Map(
    rows
      .filter((row) => row.provider === provider)
      .map((row) => [row.day, row.totalTokens])
  );
  const anchor = new Date(now);
  anchor.setHours(0, 0, 0, 0);
  const totals: Array<{ day: string; totalTokens: number }> = [];
  for (let index = dayCount - 1; index >= 0; index -= 1) {
    const date = new Date(anchor);
    date.setDate(date.getDate() - index);
    const day = [
      date.getFullYear(),
      String(date.getMonth() + 1).padStart(2, '0'),
      String(date.getDate()).padStart(2, '0')
    ].join('-');
    totals.push({ day, totalTokens: providerRows.get(day) ?? 0 });
  }
  const maximum = Math.max(1, ...totals.map((point) => point.totalTokens));
  const finalIndex = Math.max(1, totals.length - 1);
  return totals.map((point, index) => ({
    ...point,
    x: Number(((index / finalIndex) * 100).toFixed(2)),
    y: Number((28 - (point.totalTokens / maximum) * 24).toFixed(2))
  }));
}

/** A read-only SQL shape used in the review contract; the desktop backend runs it. */
export function buildUsageBreakdownQuery(query: UsageHistoryQuery = {}): string {
  const limit = Math.min(Math.max(query.limit ?? 20, 1), 200);
  const offset = Math.min(Math.max(query.offset ?? 0, 0), 100_000);
  const predicates = [
    query.provider ? `provider = '${query.provider.replaceAll("'", "''")}'` : null,
    query.model ? `model = '${query.model.replaceAll("'", "''")}'` : null,
    query.projectId ? `project_id = '${query.projectId.replaceAll("'", "''")}'` : null,
    query.workflowId ? `workflow_id = '${query.workflowId.replaceAll("'", "''")}'` : null
  ].filter((value): value is string => value !== null);
  const where = predicates.length > 0 ? predicates.join(' AND ') : '1 = 1';
  return `WITH filtered AS (SELECT provider, model, project_id, owned_id, turn_id, workflow_id, input_tokens, output_tokens FROM usage_events WHERE ${where}) SELECT provider, model, project_id, COUNT(*) AS event_count, COUNT(DISTINCT owned_id) AS session_count, COUNT(DISTINCT turn_id) AS turn_count, COUNT(DISTINCT workflow_id) AS workflow_count, SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens, COUNT(*) OVER () AS total_count FROM filtered GROUP BY provider, model, project_id ORDER BY provider ASC, model ASC LIMIT ${limit} OFFSET ${offset}`;
}
