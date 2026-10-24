import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createUnreadTracker, reloadLimit } from '../src/lib/shell/messages/agentMessagesModel.svelte.ts';

test('selecting a previously seen session counts messages without another event', async () => {
  const counts = new Map<string, number>();
  const tracker = createUnreadTracker(async (session) => counts.get(session) ?? 0);
  tracker.select('A');
  await Promise.resolve();
  tracker.seen();
  tracker.select('B');
  await Promise.resolve();
  counts.set('A', 1);
  tracker.select('A');
  await Promise.resolve();
  assert.equal(tracker.unread, 1);
  tracker.dispose();
});

test('departing count is cancelled and cannot overwrite the arriving session', async () => {
  let finishA!: (value: number) => void;
  let departingSignal!: AbortSignal;
  const pending = new Promise<number>((resolve) => { finishA = resolve; });
  const tracker = createUnreadTracker(async (session, _after, signal) => {
    if (session === 'A') { departingSignal = signal; return pending; }
    return 2;
  });
  tracker.select('A');
  tracker.select('B');
  assert.equal(tracker.unread, 0);
  assert.equal(departingSignal.aborted, true);
  await Promise.resolve();
  assert.equal(tracker.unread, 2);
  finishA(9);
  await pending;
  await Promise.resolve();
  assert.equal(tracker.unread, 2);
  tracker.dispose();
});

test('seen ignores an old count and advances only the selected session last look', async () => {
  let finish!: (value: number) => void;
  let cutoff = 0;
  const pending = new Promise<number>((resolve) => { finish = resolve; });
  const tracker = createUnreadTracker(async (_session, after) => { cutoff = after; return pending; });
  tracker.select('A');
  assert.ok(cutoff <= Date.now() && cutoff > Date.now() - 1000);
  tracker.seen();
  finish(4);
  await pending;
  await Promise.resolve();
  assert.equal(tracker.unread, 0);
  tracker.dispose();
});

test('a change recounts and disposal cancels its request', async () => {
  let calls = 0;
  let current!: AbortSignal;
  const tracker = createUnreadTracker(async (_session, _after, signal) => { calls++; current = signal; return calls; });
  tracker.select('A');
  await Promise.resolve();
  tracker.changed();
  await Promise.resolve();
  assert.equal(tracker.unread, 2);
  tracker.dispose();
  assert.equal(current.aborted, true);
  tracker.changed();
  assert.equal(calls, 2);
});

test('reload window is at least 100 and at most 500 messages', () => {
  assert.equal(reloadLimit(30), 100);
  assert.equal(reloadLimit(250), 250);
  assert.equal(reloadLimit(600), 500);
});
