import assert from 'node:assert/strict';
import {
  estimateUsageCostMicros,
  USAGE_COST_RATES,
  USAGE_COST_RATE_VERSION,
  usageRatesForModel
} from '../src/lib/shell/usage/usageCostModel.ts';

assert.match(USAGE_COST_RATE_VERSION, /^published-api-rates-/);
assert.ok(USAGE_COST_RATES.length >= 5);
assert.equal(usageRatesForModel('gpt-5.6-sol')?.outputUsdPerMillion, 30);
assert.equal(usageRatesForModel('claude-opus-4-8-20260801')?.cacheReadUsdPerMillion, 0.5);
assert.equal(usageRatesForModel('claude-haiku-4-5-20251001')?.inputUsdPerMillion, 1);
assert.equal(usageRatesForModel('gpt-5.4')?.outputUsdPerMillion, 15);
assert.equal(usageRatesForModel('gpt-5.3-codex')?.cacheReadUsdPerMillion, 0.175);
assert.equal(usageRatesForModel('gpt-5.2-codex')?.outputUsdPerMillion, 14);
assert.equal(usageRatesForModel('gpt-5.4-pro'), null);
assert.equal(usageRatesForModel('codex-auto-review'), null);
assert.equal(usageRatesForModel('unknown-model'), null);
assert.equal(usageRatesForModel('claude-fable-5'), null);

assert.deepEqual(
  estimateUsageCostMicros([
    {
      provider: 'codex',
      model: 'gpt-5.6-sol',
      inputTokens: 1_000_000,
      outputTokens: 1_000_000,
      cacheReadTokens: 1_000_000,
      cacheWriteTokens: 1_000_000,
      reasoningTokens: 9_000_000
    }
  ]),
  { estimateMicros: 41_750_000, unpricedShare: 0 }
);
assert.deepEqual(
  estimateUsageCostMicros([
    {
      provider: 'local',
      model: 'unknown-model',
      inputTokens: 1,
      outputTokens: 0,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0
    }
  ]),
  { estimateMicros: null, unpricedShare: 1 }
);
assert.deepEqual(
  estimateUsageCostMicros([
    {
      provider: 'local',
      model: 'gpt-5.6-sol',
      inputTokens: 100,
      outputTokens: 0,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0
    },
    {
      provider: 'local',
      model: 'unknown-model',
      inputTokens: 300,
      outputTokens: 0,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0
    }
  ]),
  { estimateMicros: 500, unpricedShare: 0.75 }
);
assert.deepEqual(estimateUsageCostMicros([]), { estimateMicros: 0, unpricedShare: 0 });

console.log('usage cost model tests passed');
