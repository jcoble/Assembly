import assert from 'node:assert/strict';
import { test } from 'node:test';
import { checkoutLine, formatWorkedFor, turnLine, type TurnStats } from '../src/lib/thread/turns.ts';

const stats = (over: Partial<TurnStats>): TurnStats => ({
  runId: 'r1', status: 'completed', startedAt: 0, durationMs: 12_400, firstTokenMs: 900, model: 'claude-opus-4-8',
  inputTokens: 12_000, outputTokens: 340, cacheHitPct: 81.5, costUsd: 0.0412, toolCalls: 2,
  branch: 'main', head: 'a'.repeat(40), prevBranch: 'main', prevHead: 'a'.repeat(40), ...over,
});

test('worked-for reads like Assembly', () => {
  assert.equal(formatWorkedFor(3_240), '3.2s');
  assert.equal(formatWorkedFor(42_400), '42s');
  assert.equal(formatWorkedFor(125_000), '2m 5s');
  assert.equal(formatWorkedFor(-5), '0.0s');
});

test('a finished turn shows its time, model, tokens, cache and cost', () => {
  const line = turnLine(stats({}), null, 0);
  assert.match(line, /^Worked for 12s · /);
  assert.match(line, / · claude-opus-4-8 · 12k in · 340 out · 82% cached · \$0\.041$/);
});

test('a live turn shows what is known so far', () => {
  assert.equal(turnLine(undefined, 1_000, 6_000), 'Working for 5.0s');
  assert.match(turnLine(stats({ status: 'running', durationMs: null, startedAt: 1_000, model: 'm' }), 1_000, 31_000), /^Working for 30s · .* · m/);
});

test('a failed turn says so', () => {
  assert.match(turnLine(stats({ status: 'failed', durationMs: null, inputTokens: null, outputTokens: null, costUsd: null, cacheHitPct: null }), null, 0), /^Failed · /);
});

test('the checkout line shows only when the checkout moved between turns', () => {
  assert.equal(checkoutLine(undefined), null);
  assert.equal(checkoutLine(stats({})), null);
  assert.equal(checkoutLine(stats({ prevHead: null, prevBranch: null })), null);
  assert.equal(checkoutLine(stats({ branch: 'feature' })), 'Checkout changed · main@aaaaaaa → feature@aaaaaaa');
  assert.equal(checkoutLine(stats({ branch: 'HEAD', head: 'b'.repeat(40) })), 'Checkout changed · main@aaaaaaa → bbbbbbb');
});
