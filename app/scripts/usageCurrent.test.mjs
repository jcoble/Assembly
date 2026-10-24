import assert from 'node:assert/strict';
import { normalizeProviderUsage, usageDisplayLabel, usageDisplayPercent, usageQuotaWindowLabel, usageWindowDurationLabel } from '../src/lib/shell/usage/usageCurrent.ts';

const unavailable = normalizeProviderUsage({ provider: 'local', state: 'unavailable', reason: 'No quota data' });
assert.equal(unavailable.state, 'unavailable');
assert.equal(unavailable.windows.length, 0);
assert.equal(normalizeProviderUsage({ provider: 'local', inputTokens: 1000 }).state, 'unavailable');

const weeklyOnly = normalizeProviderUsage({
  provider: 'codex',
  instanceId: 'local',
  state: 'available',
  windows: [
    {
      name: '5-hour',
      semantics: 'Provider-defined 10080-minute quota window',
      percentConsumed: 42,
      resetAt: '1786160562'
    }
  ]
});
assert.deepEqual(weeklyOnly.windows.map((window) => window.label), ['Weekly']);
assert.equal(weeklyOnly.windows[0].windowMinutes, 10080);
assert.equal(usageQuotaWindowLabel(weeklyOnly.windows[0]), 'Weekly (7-day) window');

const fiveHourAndWeekly = normalizeProviderUsage({
  provider: 'codex',
  instanceId: 'local',
  state: 'available',
  windows: [
    { name: 'primary', semantics: 'Provider-defined 300-minute quota window', percentConsumed: 12, resetAt: '1783741721' },
    { name: 'secondary', semantics: 'Provider-defined 10080-minute quota window', percentConsumed: 17, resetAt: '1784310518' }
  ]
});
assert.deepEqual(fiveHourAndWeekly.windows.map((window) => window.label), ['5-hour', 'Weekly']);
assert.equal(usageWindowDurationLabel(720), '12-hour');
assert.equal(usageQuotaWindowLabel(fiveHourAndWeekly.windows[0]), 'Session (5-hour) window');
// A quota can be read either way round, and both readings stay inside 0–100.
assert.equal(usageDisplayLabel({ usedPercent: 37 }, 'used'), '37% used');
assert.equal(usageDisplayLabel({ usedPercent: 37 }, 'remaining'), '63% left');
assert.equal(usageDisplayPercent({ usedPercent: 140 }, 'remaining'), 0);
assert.equal(usageDisplayPercent({ usedPercent: -5 }, 'remaining'), 100);

console.log('current usage tests passed');
