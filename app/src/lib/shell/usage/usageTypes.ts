export type ProviderUsageState = 'available' | 'unavailable' | 'error';

export type ProviderUsageWindow = {
  label: string;
  usedPercent: number;
  resetsAt: string | null;
  windowMinutes?: number | null;
};

export type ProviderUsageSnapshot = {
  provider: string;
  account: string | null;
  instanceId: string;
  state: ProviderUsageState;
  windows: ProviderUsageWindow[];
  capturedAt: number;
  source: string | null;
  sourceVersion: string | null;
  unavailableReason: string | null;
};

export type UsageHistoryQuery = {
  provider?: string | null;
  model?: string | null;
  projectId?: string | null;
  workflowId?: string | null;
  startMicros?: number | null;
  endMicros?: number | null;
  limit?: number | null;
  offset?: number | null;
};

export type UsageRange = 'today' | 'yesterday' | '30-days' | 'all';

export type UsageTokenBreakdown = {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
  totalTokens: number;
};

export type UsageCostInputRow = {
  provider: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
};

export type UsageSummary = {
  eventCount: number;
  providerCount: number;
  activeDays: number;
  sessionCount: number;
  turnCount: number;
  workflowCount: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
  totalTokens: number;
  cacheSharePercent: number;
  estimatedCostMicros: number | null;
};

export type UsageBreakdownRow = {
  provider: string;
  model: string;
  projectId: string | null;
  eventCount: number;
  sessionCount: number;
  turnCount: number;
  workflowCount: number;
  inputTokens: number;
  outputTokens: number;
  totalCount: number;
};

export type UsageProviderSummaryRow = {
  provider: string;
  modelCount: number;
  eventCount: number;
  sessionCount: number;
  turnCount: number;
  workflowCount: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
  estimatedCostMicros: number | null;
  unpricedPercent: number;
  totalTokens: number;
  rangeTotalTokens: number;
  rangeEstimatedCostMicros: number | null;
  rangeUnpricedPercent: number;
  rangeSharePercent: number;
  lastModel: string;
  lastSessionId: string | null;
  lastProjectId: string | null;
  lastSeenAtMicros: number | null;
};

export type UsageDailyRow = {
  day: string;
  provider: string;
  model: string;
  projectId: string | null;
  eventCount: number;
  sessionCount: number;
  turnCount: number;
  workflowCount: number;
  inputTokens: number;
  outputTokens: number;
  estimatedCostMicros: number | null;
};

export type UsageDailyTotalsRow = {
  day: string;
  eventCount: number;
  sessionCount: number;
  turnCount: number;
  workflowCount: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
  estimatedCostMicros: number | null;
  totalTokens: number;
  rangeMaxTokens: number;
  bestDay: string;
};

export type UsageProviderDailyTotalsRow = Omit<UsageDailyTotalsRow, 'rangeMaxTokens' | 'bestDay'> & {
  provider: string;
};
