import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';
import { get } from 'svelte/store';

const sourcePath = fileURLToPath(
  new URL('../src/lib/shell/conversation/sessionPresence.ts', import.meta.url)
);
const outputPath = fileURLToPath(
  new URL('../src/lib/shell/conversation/.sessionPresence.test.mjs', import.meta.url)
);
const source = readFileSync(sourcePath, 'utf8');
writeFileSync(outputPath, stripTypeScriptTypes(source, { mode: 'strip' }));

let presence;
try {
  presence = await import(`${outputPath}?test=${Date.now()}`);
} finally {
  rmSync(outputPath, { force: true });
}

const empty = presence.EMPTY_SESSION_PRESENCE_HISTORY;
const connected = { terminalState: 'live', connectionState: 'connected' };

assert.deepEqual(
  presence.deriveSessionPresence(connected, empty, 10_000),
  { state: 'idle', elapsedMs: null }
);

const started = presence.reduceSessionPresenceHistory(empty, {
  ownedId: 'session-a', kind: 'turn-started', timestampMs: 10_000, turnId: 'turn-a'
});
assert.deepEqual(
  presence.deriveSessionPresence({ ...connected, activeTurnId: 'turn-a' }, started, 75_400),
  { state: 'working', elapsedMs: 65_400 }
);
assert.equal(presence.formatPresenceElapsed(65_400), '01:05');
assert.equal(presence.formatPresenceElapsed(3_661_000), '61:01');

const duplicateStart = presence.reduceSessionPresenceHistory(started, {
  ownedId: 'session-a', kind: 'turn-started', timestampMs: 20_000, turnId: 'turn-a'
});
assert.equal(duplicateStart.turnStartedAt, 10_000, 'duplicate start events keep the original timer');

const approval = presence.reduceSessionPresenceHistory(started, {
  ownedId: 'session-a', kind: 'approval-requested', timestampMs: 30_000, turnId: 'turn-a'
});
assert.equal(
  presence.deriveSessionPresence(
    { ...connected, activeTurnId: 'turn-a', pendingApprovalCount: 1 },
    approval,
    31_000
  ).state,
  'needs-attention',
  'an unacknowledged approval takes precedence over an active turn'
);

const acknowledgedApproval = presence.acknowledgePresenceHistory(approval, 31_000);
assert.equal(
  presence.deriveSessionPresence(
    { ...connected, activeTurnId: 'turn-a', pendingApprovalCount: 1 },
    acknowledgedApproval,
    32_000
  ).state,
  'idle',
  'an acknowledged approval is visible but is not active work'
);
assert.equal(
  presence.acknowledgePresenceHistory(approval, 1).lastAckedAt,
  approval.lastAttentionAt,
  'acknowledgement clears attention even when a provider timestamp is ahead of the local clock'
);

const finished = presence.reduceSessionPresenceHistory(started, {
  ownedId: 'session-a', kind: 'turn-finished', timestampMs: 80_000, turnId: 'turn-a'
});
assert.equal(presence.deriveSessionPresence(connected, finished, 81_000).state, 'needs-attention');
assert.equal(
  presence.deriveSessionPresence(
    connected,
    presence.acknowledgePresenceHistory(finished, 82_000),
    83_000
  ).state,
  'idle'
);

assert.equal(
  presence.deriveSessionPresence({ ...connected, terminalState: 'exited' }, empty, 0).state,
  'disconnected'
);
assert.equal(
  presence.deriveSessionPresence({ ...connected, connectionState: 'failed' }, empty, 0).state,
  'disconnected'
);
assert.equal(
  presence.deriveSessionPresence({ ...connected, connectionState: 'disconnected' }, empty, 0).state,
  'disconnected'
);
assert.equal(
  presence.deriveSessionPresence(
    {
      ...connected,
      connectionState: 'disconnected',
      suspended: true,
      activeTurnId: 'stale-turn',
      pendingApprovalCount: 1
    },
    approval,
    0
  ).state,
  'idle',
  'a healthy suspended session stays idle despite stale work signals instead of looking stopped'
);
assert.equal(
  presence.deriveSessionPresence(
    { ...connected, connectionState: 'failed', suspended: true },
    empty,
    0
  ).state,
  'disconnected',
  'suspension does not hide an unhealthy connection'
);
assert.equal(
  presence.deriveSessionPresence(
    { ...connected, connectionState: 'disconnected', runtimeState: 'starting' },
    empty,
    0
  ).state,
  'idle',
  'a session that is still starting does not offer restart prematurely'
);
assert.equal(
  presence.deriveSessionPresence({ ...connected, sending: true }, empty, 1_000).state,
  'working'
);

assert.deepEqual(
  presence.sessionPresenceEventFromConversation({
    ownedId: 'session-b', timestampMs: 100, type: 'turn.started', turnId: 'turn-b', payload: {}
  }),
  { ownedId: 'session-b', kind: 'turn-started', timestampMs: 100, turnId: 'turn-b' }
);
assert.equal(
  presence.sessionPresenceEventFromConversation({
    ownedId: 'session-b', timestampMs: 110, type: 'content.delta', payload: { channel: 'assistant' }
  }),
  null
);

presence.clearSessionPresence();
presence.recordSessionPresenceEvent({
  ownedId: 'session-c', kind: 'turn-finished', timestampMs: 200, turnId: 'turn-c'
});
assert.equal(get(presence.sessionPresenceHistory)['session-c'].lastAttentionAt, 200);
presence.setViewedSession('session-c', 250);
assert.equal(get(presence.sessionPresenceHistory)['session-c'].lastAckedAt, 250);
presence.recordSessionPresenceEvent({
  ownedId: 'session-c', kind: 'approval-requested', timestampMs: 300, turnId: 'turn-c'
});
assert.equal(
  get(presence.sessionPresenceHistory)['session-c'].lastAckedAt,
  300,
  'attention arriving in the viewed session is acknowledged immediately'
);
presence.clearSessionPresence();

console.log('session presence tests passed');
