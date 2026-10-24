import assert from 'node:assert/strict';
import {
  buildUsageBreakdownQuery,
  buildProviderUsageTrend,
  buildUsageHeatmap,
  usageCacheShare,
  usageHeatmapQuery,
  usageRangeQuery,
  usageSummaryLabel,
  usageTotalTokens
} from '../src/lib/shell/usage/usageAnalytics.ts';

assert.equal(usageSummaryLabel({ inputTokens: 10, outputTokens: 4 }), '14 tokens');
const query = buildUsageBreakdownQuery({ provider: 'provider-a', limit: 20, offset: 0 });
assert.match(query, /GROUP BY/);
assert.match(query, /LIMIT/);

const now = new Date(2026, 7, 9, 12, 0, 0);
const yesterday = usageRangeQuery('yesterday', now);
assert.equal((yesterday.endMicros - yesterday.startMicros) / 1_000, 86_400_000);
assert.deepEqual(usageRangeQuery('all', now), {});
assert.equal((usageHeatmapQuery('all', now).endMicros - usageHeatmapQuery('all', now).startMicros) / 1_000, 42 * 86_400_000);

const totalTokens = usageTotalTokens({ inputTokens: 10, outputTokens: 4, cacheReadTokens: 3, cacheWriteTokens: 2 });
assert.equal(totalTokens, 19);
assert.equal(usageCacheShare({ cacheReadTokens: 3, cacheWriteTokens: 2, totalTokens }), 26);

const heatmap = buildUsageHeatmap([
  {
    day: '2026-08-09',
    eventCount: 1,
    sessionCount: 1,
    turnCount: 1,
    workflowCount: 0,
    inputTokens: 10,
    outputTokens: 5,
    cacheReadTokens: 5,
    cacheWriteTokens: 0,
    reasoningTokens: 3,
    estimatedCostMicros: null,
    totalTokens: 20,
    rangeMaxTokens: 20,
    bestDay: '2026-08-09'
  }
], 'all', now);
assert.equal(heatmap.length, 42);
assert.deepEqual(heatmap.at(-1), { day: '2026-08-09', totalTokens: 20, intensity: 4 });
assert.equal(heatmap[0].intensity, 0);
assert.equal(buildUsageHeatmap([], 'today', now).length, 42);

const trend = buildProviderUsageTrend([
  { day: '2026-08-09', provider: 'codex', totalTokens: 200 },
  { day: '2026-08-09', provider: 'claude', totalTokens: 800 }
], 'codex', now, 3);
assert.equal(trend.length, 3);
assert.deepEqual(trend.at(-1), { day: '2026-08-09', totalTokens: 200, x: 100, y: 4 });
assert.equal(trend[0].totalTokens, 0);
console.log('usage analytics tests passed');
