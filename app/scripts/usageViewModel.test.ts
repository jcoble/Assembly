import assert from 'node:assert/strict';
import fs from 'node:fs';

globalThis.$state = <T>(value: T): T => value;
const { usagePercent, usageProviderLabel, usageResetLabel } = await import('../src/lib/shell/usage/usageCurrent.ts');
const { usageSummaryLabel } = await import('../src/lib/shell/usage/usageAnalytics.ts');
const { describeUsageError, settleUsageRequest, usageState } = await import('../src/lib/shell/usage/usageStore.svelte.ts');

function testUsageErrorsPreserveStringRejections() {
  // describeUsageError must pass Tauri string rejections through untouched.
  assert.equal(describeUsageError('Usage history database could not open: disk failure'), 'Usage history database could not open: disk failure');
  assert.equal(describeUsageError(new Error('boom')), 'boom');
  assert.equal(describeUsageError({ weird: true }), 'Usage history could not be read.');
}

testUsageErrorsPreserveStringRejections();

usageState.currentLoading = true;
assert.equal(usageState.loading, true);
usageState.currentLoading = false;
usageState.historyLoading = true;
assert.equal(usageState.loading, true);
usageState.historyLoading = false;
assert.equal(usageState.loading, false);
await assert.rejects(
  settleUsageRequest(Promise.reject(new Error('usage read failed'))),
  /usage read failed/
);

assert.equal(usageProviderLabel('codex'), 'Codex');
assert.equal(usagePercent({ usedPercent: 75 }), 75);
assert.equal(usagePercent({ usedPercent: 125 }), 100);
assert.match(usageResetLabel(String(Date.now() + 3_600_000)), /^Resets in 1h/);
assert.equal(usageSummaryLabel({ inputTokens: 12, outputTokens: 8 }), '20 tokens');

const usageWorkspaceSource = fs.readFileSync(new URL('../src/lib/shell/usage/UsageWorkspace.svelte', import.meta.url), 'utf8');
const usagePopoverSource = fs.readFileSync(new URL('../src/lib/shell/usage/UsagePopover.svelte', import.meta.url), 'utf8');
const usageStoreSource = fs.readFileSync(new URL('../src/lib/shell/usage/usageStore.svelte.ts', import.meta.url), 'utf8');
const usageBackendSource = fs.readFileSync(new URL('../src/lib/shell/usage/usageBackend.ts', import.meta.url), 'utf8');
const usageDbSource = fs.readFileSync(new URL('../src-tauri/src/usage_db.rs', import.meta.url), 'utf8');
const mainSource = fs.readFileSync(new URL('../src-tauri/src/main.rs', import.meta.url), 'utf8');
const tauriSource = fs.readFileSync(new URL('../src/lib/tauriSource.ts', import.meta.url), 'utf8');
assert.match(usageWorkspaceSource, /usageState\.providerSummary/);
assert.match(usageWorkspaceSource, /usageState\.providerRollups/);
assert.match(usageWorkspaceSource, /usageState\.providerDailyTotals/);
assert.match(usageWorkspaceSource, /rangeEstimatedCostMicros/);
assert.match(usageWorkspaceSource, /rangeUnpricedPercent/);
assert.match(usageWorkspaceSource, /rangeSharePercent/);
assert.match(usageWorkspaceSource, /usageRangeOptions/);
assert.match(usageWorkspaceSource, /Best: \{formatDay\(bestDay\)\}/);
assert.match(usageWorkspaceSource, /donut-segment/);
assert.match(usageWorkspaceSource, /buildProviderUsageTrend/);
assert.match(usageWorkspaceSource, /grid-template-columns: repeat\(6, 14px\)/);
assert.match(usageWorkspaceSource, /grid-template-rows: repeat\(7, 14px\)/);
assert.match(usageWorkspaceSource, /Costs are estimates, not billing totals/);
assert.doesNotMatch(usageWorkspaceSource, /usageState\.daily\.slice\(\)\.reverse\(\)/);
assert.doesNotMatch(usageWorkspaceSource, /\.metric-grid strong \{[^}]*text-overflow/s);
assert.match(usagePopoverSource, /data-testid="usage-modal"/);
assert.match(usagePopoverSource, /event\.key === 'Escape'/);
assert.match(usagePopoverSource, /min-width: min\(900px/);
assert.match(usagePopoverSource, /class="details"[\s\S]*?openStats\(\)[\s\S]*?>Open Stats &amp; Usage</);
assert.match(usagePopoverSource, /function openStats\(\)[\s\S]*?open = false;[\s\S]*?fullOpen = true;/);
assert.match(usagePopoverSource, /<\/aside>[\s\S]*\{#if fullOpen\}[\s\S]*<UsageWorkspace/);
assert.doesNotMatch(usagePopoverSource, /minute provider window/);
assert.match(usageStoreSource, /dailyTotals/);
assert.match(usageStoreSource, /providerRollups/);
assert.match(usageStoreSource, /readProviderDailyTotals/);
assert.match(usageStoreSource, /get loading\(\) \{ return this\.currentLoading \|\| this\.historyLoading \|\| this\.historyRefreshing; \}/);
assert.match(usageStoreSource, /historyRefreshing/);
assert.match(usageStoreSource, /const cached = await loadUsageHistory\(usageState\.range\);[\s\S]*refreshUsageIndexInBackground\(\);/);
assert.doesNotMatch(usageStoreSource, /await settleUsageRequest\(usageService\.refreshHistory\(\)\)/);
assert.match(usageStoreSource, /finally \{\s*usageState\.currentLoading = false;/);
assert.match(usageStoreSource, /finally \{\s*usageState\.historyLoading = false;/);
assert.match(usageStoreSource, /readDailyTotals/);
assert.match(usageStoreSource, /No[\s\S]*event or turn rows cross IPC/);
assert.match(usageStoreSource, /selectUsageRange/);
assert.match(tauriSource, /read_usage_daily_totals/);
assert.match(usageBackendSource, /read_usage_token_breakdown/);
assert.match(usageBackendSource, /read_usage_provider_daily_totals/);
assert.match(usageBackendSource, /read_usage_cost_inputs/);
assert.match(usageDbSource, /MAX\(total_tokens\) OVER \(\)/);
assert.match(usageDbSource, /ROW_NUMBER\(\) OVER \(PARTITION BY provider/);
assert.match(usageDbSource, /MAX_USAGE_QUERY_ROWS: usize = 200/);
assert.match(usageDbSource, /provider_costs AS/);
assert.doesNotMatch(usageDbSource, /json_group_array\(json_object/);
assert.match(mainSource, /read_usage_token_breakdown,/);
assert.match(mainSource, /read_usage_provider_daily_totals,/);
assert.match(mainSource, /read_usage_cost_inputs,/);
console.log('usage view-model tests passed');
