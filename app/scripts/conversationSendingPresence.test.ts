import assert from 'node:assert/strict';
import { test } from 'node:test';
import { get } from 'svelte/store';
import { store } from './agentConversationStore.test.ts';
import { sessionPresenceHistory, synchronizeSessionPresenceWork } from '../src/lib/shell/conversation/sessionPresence.ts';

test('a cancelled revive clears its sending placeholder after suspension or release', () => {
  const ownedId = 'cancelled-revive-presence';
  const connection = { ownedId, provider: 'claude', generation: 1, state: 'connected' };
  store.setConversationConnection(connection);
  store.setConversationSending(ownedId, true);
  assert.equal(get(sessionPresenceHistory)[ownedId].activeTurnId, 'sending');
  store.setConversationConnection({ ...connection, suspended: true });
  store.setConversationSending(ownedId, false);
  assert.equal(get(sessionPresenceHistory)[ownedId].activeTurnId, null);
  store.setConversationSending(ownedId, true);
  store.evictConversationSession(ownedId);
  store.setConversationSending(ownedId, false);
  assert.equal(get(sessionPresenceHistory)[ownedId].activeTurnId, null);
});

test('ending a send preserves the presence of a real turn', () => {
  const ownedId = 'real-turn-presence';
  synchronizeSessionPresenceWork(ownedId, 'real-turn', false);
  store.setConversationSending(ownedId, false);
  assert.equal(get(sessionPresenceHistory)[ownedId].activeTurnId, 'real-turn');
});
