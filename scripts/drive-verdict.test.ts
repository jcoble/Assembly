import assert from 'node:assert/strict';
import { test } from 'node:test';
import { isLimit, providerBlock } from './drive/verdict.ts';
import type { Row } from './drive/live-rows.ts';

const row = (kind: string, payload: Record<string, unknown>): Row => ({ seq: 1, prevSeq: 0, firstSeq: 1, kind, runId: 'r', payload });

test('a provider that refuses the login or hits a limit blocks the case', () => {
  assert.ok(providerBlock([row('turn', { state: 'failed', authFailure: { version: 2 } })]));
  assert.ok(providerBlock([row('user', { text: 'hi', authBlocker: { effectId: 'e' } })]));
  const expired = providerBlock([row('error', { message: 'Authentication required: Failed to authenticate: OAuth session expired and could not be refreshed', code: null })]);
  assert.match(expired!, /OAuth session expired/);
  assert.equal(isLimit(expired!), false);
  const limited = providerBlock([row('error', { message: 'You have hit the rate limit, try again later', code: null })]);
  assert.ok(limited);
  assert.equal(isLimit(limited!), true);
});

test('an app failure is not a provider block', () => {
  assert.equal(providerBlock([row('turn', { state: 'finished' })]), undefined);
  assert.equal(providerBlock([row('tool', { name: 'Bash', state: 'failed', summary: 'python3 -c ...' })]), undefined);
  assert.equal(providerBlock([row('error', { message: 'The agent stopped before the turn finished.', code: 'agent_lost' })]), undefined);
});
