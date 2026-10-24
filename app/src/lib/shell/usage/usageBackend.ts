import {
  isTauriRuntime,
  readCurrentProviderUsageFromTauri,
  readUsageBreakdownFromTauri,
  readUsageDailyFromTauri,
  readUsageDailyTotalsFromTauri,
  readUsageProviderSummaryFromTauri,
  readUsageSummaryFromTauri,
  refreshUsageHistoryFromTauri
} from '../../tauriSource.ts';
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
import { normalizeProviderUsage } from './usageCurrent.ts';

export async function readCurrentProviderUsage(
  provider: string | null,
  instanceId: string | null
): Promise<ProviderUsageSnapshot | null> {
  const snapshot = await readCurrentProviderUsageFromTauri(provider, instanceId);
  return snapshot ? normalizeProviderUsage(snapshot) : null;
}

export async function readUsageSummary(query: UsageHistoryQuery = {}): Promise<UsageSummary | null> {
  return readUsageSummaryFromTauri(query);
}

export async function readUsageBreakdown(query: UsageHistoryQuery = {}): Promise<UsageBreakdownRow[] | null> {
  return readUsageBreakdownFromTauri(query);
}

export async function readUsageProviderSummary(query: UsageHistoryQuery = {}): Promise<UsageProviderSummaryRow[] | null> {
  return readUsageProviderSummaryFromTauri(query);
}

export async function readUsageDaily(query: UsageHistoryQuery = {}): Promise<UsageDailyRow[] | null> {
  return readUsageDailyFromTauri(query);
}

export async function readUsageDailyTotals(query: UsageHistoryQuery = {}): Promise<UsageDailyTotalsRow[] | null> {
  return readUsageDailyTotalsFromTauri(query);
}

async function readUsageQueryFromTauri<T>(command: string, query: UsageHistoryQuery): Promise<T | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<T>(command, { query });
}

export async function readUsageTokenBreakdown(query: UsageHistoryQuery = {}): Promise<UsageTokenBreakdown | null> {
  return readUsageQueryFromTauri('read_usage_token_breakdown', query);
}

export async function readUsageProviderDailyTotals(query: UsageHistoryQuery = {}): Promise<UsageProviderDailyTotalsRow[] | null> {
  return readUsageQueryFromTauri('read_usage_provider_daily_totals', query);
}

export async function readUsageCostInputs(query: UsageHistoryQuery = {}): Promise<UsageCostInputRow[] | null> {
  return readUsageQueryFromTauri('read_usage_cost_inputs', query);
}

export async function refreshUsageHistory(): Promise<number | null> {
  return refreshUsageHistoryFromTauri();
}
