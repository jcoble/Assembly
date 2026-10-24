import {
  readCurrentProviderUsage,
  readUsageBreakdown,
  readUsageCostInputs,
  readUsageDaily,
  readUsageDailyTotals,
  readUsageProviderDailyTotals,
  readUsageProviderSummary,
  readUsageSummary,
  readUsageTokenBreakdown,
  refreshUsageHistory
} from './usageBackend.ts';
import type {
  ProviderUsageSnapshot,
  UsageBreakdownRow,
  UsageCostInputRow,
  UsageDailyRow,
  UsageDailyTotalsRow,
  UsageHistoryQuery,
  UsageProviderDailyTotalsRow,
  UsageProviderSummaryRow,
  UsageSummary,
  UsageTokenBreakdown
} from './usageTypes.ts';

const currentInFlight = new Map<string, Promise<ProviderUsageSnapshot | null>>();

export const usageService = {
  async readCurrent(provider: string | null, instanceId: string | null): Promise<ProviderUsageSnapshot | null> {
    const key = `${provider ?? 'unknown'}:${instanceId ?? 'unknown'}`;
    const existing = currentInFlight.get(key);
    if (existing) {
      const snapshot = await existing;
      return snapshot;
    }
    const request = readCurrentProviderUsageOnce(key, provider, instanceId);
    currentInFlight.set(key, request);
    const snapshot = await request;
    return snapshot;
  },
  async readSummary(query?: UsageHistoryQuery): Promise<UsageSummary | null> {
    const summary = await readUsageSummary(query);
    return summary;
  },
  async readBreakdown(query?: UsageHistoryQuery): Promise<UsageBreakdownRow[] | null> {
    const breakdown = await readUsageBreakdown(query);
    return breakdown;
  },
  async readProviderSummary(query?: UsageHistoryQuery): Promise<UsageProviderSummaryRow[] | null> {
    const summary = await readUsageProviderSummary(query);
    return summary;
  },
  async readDaily(query?: UsageHistoryQuery): Promise<UsageDailyRow[] | null> {
    const daily = await readUsageDaily(query);
    return daily;
  },
  async readDailyTotals(query?: UsageHistoryQuery): Promise<UsageDailyTotalsRow[] | null> {
    const totals = await readUsageDailyTotals(query);
    return totals;
  },
  async readTokenBreakdown(query?: UsageHistoryQuery): Promise<UsageTokenBreakdown | null> {
    const breakdown = await readUsageTokenBreakdown(query);
    return breakdown;
  },
  async readProviderDailyTotals(query?: UsageHistoryQuery): Promise<UsageProviderDailyTotalsRow[] | null> {
    const totals = await readUsageProviderDailyTotals(query);
    return totals;
  },
  async readCostInputs(query?: UsageHistoryQuery): Promise<UsageCostInputRow[] | null> {
    const inputs = await readUsageCostInputs(query);
    return inputs;
  },
  async refreshHistory(): Promise<number | null> {
    const refreshed = await refreshUsageHistory();
    return refreshed;
  }
};

async function readCurrentProviderUsageOnce(
  key: string,
  provider: string | null,
  instanceId: string | null
): Promise<ProviderUsageSnapshot | null> {
  try {
    return await readCurrentProviderUsage(provider, instanceId);
  } finally {
    currentInFlight.delete(key);
  }
}
