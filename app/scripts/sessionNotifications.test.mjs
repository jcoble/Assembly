import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';

const sourcePath = fileURLToPath(
  new URL('../src/lib/shell/conversation/sessionNotifications.ts', import.meta.url)
);
const outputPath = fileURLToPath(
  new URL('../src/lib/shell/conversation/.sessionNotifications.test.mjs', import.meta.url)
);
const source = readFileSync(sourcePath, 'utf8');
writeFileSync(outputPath, stripTypeScriptTypes(source, { mode: 'strip' }));

let notifications;
try {
  notifications = await import(`${outputPath}?test=${Date.now()}`);
} finally {
  rmSync(outputPath, { force: true });
}

const idle = {
  activeTurnId: null,
  turnStartedAt: null,
  lastAttentionAt: null,
  lastAckedAt: null
};
const attention = { ...idle, lastAttentionAt: 100 };
const stillAttention = { ...attention, lastAttentionAt: 200 };
const acknowledged = { ...stillAttention, lastAckedAt: 200 };

assert.equal(notifications.enteredNeedsAttention(idle, attention), true);
assert.equal(notifications.enteredNeedsAttention(attention, stillAttention), false);
assert.equal(notifications.enteredNeedsAttention(stillAttention, acknowledged), false);
assert.equal(notifications.enteredNeedsAttention(acknowledged, { ...acknowledged, lastAttentionAt: 300 }), true);

assert.equal(
  notifications.sessionNotificationBody('turn-finished'),
  'Finished — needs your attention'
);
assert.equal(
  notifications.sessionNotificationBody('approval-requested'),
  'Waiting for approval'
);

const sent = [];
const dispatch = notifications.createSessionNotificationDispatcher((notification) => {
  sent.push(notification);
});
const base = {
  ownedId: 'session-a',
  kind: 'turn-finished',
  previous: idle,
  next: attention,
  title: 'Release checks',
  provider: 'agent',
  sessionViewed: false,
  windowFocused: true
};

assert.equal(await dispatch(base), true);
assert.deepEqual(sent, [{
  title: 'Release checks',
  body: 'Finished — needs your attention'
}]);

assert.equal(await dispatch({ ...base, previous: attention, next: stillAttention }), false);
assert.equal(sent.length, 1, 'a session is not notified again while it stays needs-attention');

assert.equal(await dispatch({
  ...base,
  ownedId: 'session-b',
  kind: 'approval-requested',
  title: null,
  provider: 'worker',
  sessionViewed: false,
  windowFocused: false
}), true);
assert.deepEqual(sent.at(-1), { title: 'Worker', body: 'Waiting for approval' });

assert.equal(await dispatch({
  ...base,
  ownedId: 'session-c',
  sessionViewed: true,
  windowFocused: true
}), false);
assert.equal(sent.length, 2, 'the viewed session stays quiet while the app window is focused');

console.log('session notification tests passed');
