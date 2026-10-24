/// <reference types="node" />

// After a remote server restarts mid-turn, the reconnect inventory read is the
// only thing that can end the turn the new server no longer runs. A replayed
// event arriving during that read must not leave Working on for good.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { get } from 'svelte/store';
import { sessionPresenceHistory, synchronizeSessionPresenceWork } from '../src/lib/shell/conversation/sessionPresence.ts';

const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
const block = source.slice(source.indexOf('async function refreshRemoteSessionActivity('), source.indexOf('// A session an agent started'));
const code = stripTypeScriptTypes(block, { mode: 'strip' });

function harness(ownedId: string) {
  const row = { ownedId, state: 'live', runtimeState: 'working', activeTurnId: 'turn-1' as string | null };
  const chat = { activeTurnId: 'turn-1' as string | undefined, suspended: false, sending: true };
  const reads: Array<(records: unknown[]) => void> = [];
  let readApplied: () => void = () => {};
  const dependencies = {
    rail: { remoteConnections: { workbox: 'connected' }, error: null },
    listRemoteAgentConversationSessionsFromTauri: () => new Promise<unknown[]>((resolve) => { reads.push(resolve); }),
    // Every reply ends with a bystander session no event touches, so its
    // update marks the moment a read has been fully applied.
    updateOwnedSession: (id: string, patch: Partial<typeof row>) => {
      if (id === ownedId) Object.assign(row, patch);
      else readApplied();
    },
    getConversationSession: (id: string) => id === ownedId ? chat : null,
    preparingSends: new Map(),
    synchronizeSessionPresenceWork,
    setConversationSending: (_id: string, sending: boolean) => { chat.sending = sending; }
  };
  const api = Function(...Object.keys(dependencies), `
    let conversationEventsDisposed = false;
    let conversationEventsGeneration = 1;
    let remoteActivityController = null;
    const railActivityEvents = new Map();
    let sequence = 0;
    ${code}
    return { refresh: () => refreshRemoteSessionActivity(1),
      eventArrives: () => railActivityEvents.set(${JSON.stringify(ownedId)}, { generation: 1, sequence: ++sequence }) };
  `)(...Object.values(dependencies)) as { refresh(): Promise<void>; eventArrives(): void };
  const record = (id: string, activeTurnId: string | null) => ({
    ownedId: id, remoteProfileId: 'workbox', activeTurnId, state: activeTurnId ? 'working' : 'suspended',
    suspended: !activeTurnId, pendingPermission: false, pendingInput: false, backgroundWork: []
  });
  // Answers read number `index` with this session's state and waits until it is applied.
  const answer = async (index: number, activeTurnId: string | null) => {
    const applied = new Promise<void>((resolve) => { readApplied = resolve; });
    reads[index]([record(ownedId, activeTurnId), record('bystander', null)]);
    await applied;
  };
  synchronizeSessionPresenceWork(ownedId, 'turn-1', true, 1);
  return { row, chat, reads, api, answer };
}

await test('a restarted server clears Working even when an event raced the reconnect read', async () => {
  const { row, chat, reads, api, answer } = harness('restarted-mid-turn');
  void api.refresh();
  api.eventArrives();
  await answer(0, null);
  assert.equal(row.activeTurnId, 'turn-1', 'the raced reply is older than the event, so it is not applied');
  assert.equal(reads.length, 2, 'a second read follows');
  await answer(1, null);
  assert.equal(row.activeTurnId, null, 'the rail spinner clears');
  assert.equal(chat.activeTurnId, undefined, 'Stop clears');
  assert.equal(chat.sending, false, 'the composer can send again');
  assert.equal(get(sessionPresenceHistory)['restarted-mid-turn'].activeTurnId, null);
});

await test('a turn still running after a network drop keeps Working through the second read', async () => {
  const { row, chat, api, answer } = harness('still-running');
  void api.refresh();
  api.eventArrives();
  await answer(0, 'turn-1');
  await answer(1, 'turn-1');
  assert.equal(row.activeTurnId, 'turn-1');
  assert.equal(row.runtimeState, 'working');
  assert.equal(chat.activeTurnId, 'turn-1');
  assert.equal(chat.sending, true);
});

await test('events racing both reads stop after the second read', async () => {
  const { reads, api, answer } = harness('busy-session');
  void api.refresh();
  api.eventArrives();
  await answer(0, 'turn-1');
  api.eventArrives();
  await answer(1, 'turn-1');
  assert.equal(reads.length, 2);
});
