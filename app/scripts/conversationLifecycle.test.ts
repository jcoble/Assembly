import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
function extract(name: string): string {
  const match = source.match(new RegExp(`^(?:export )?(?:async )?function ${name}\\([\\s\\S]*?^}$`, 'm'));
  assert.ok(match, name);
  return stripTypeScriptTypes(match[0].replace(/^export /, ''));
}
function deferred<T = void>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
const globals = `
let conversationStream = null, unlistenTitles = null, unlistenRemoteConnections = null;
let conversationEventsSetup = null, conversationEventsDisposed = false, conversationEventsGeneration = 0;
let conversationEventsController = null, remoteActivityController = null, remoteActivityRead = 0;
const childHistoryReads = new Map(), railActivityEvents = new Map(), sessionsNotFound = new Map(), terminalProjections = new Map();
let selectedConversationRead = null, childConversationRead = null, adoptController = new AbortController();
`;
function eventHarness(holdRegistration = false, holdListener?: string, holdInventory = false) {
  const environment = deferred<any>(), requested = deferred(), unregister = deferred();
  const registrationReply = deferred(), registering = deferred();
  const listenerReply = deferred(), listening = deferred();
  const inventory = deferred();
  let listeners = 0, unregisters = 0, updates = 0, reloads = 0;
  const handlers = new Map<string, Function>();
  const rail = { remoteProfiles: [], remoteConnections: {} };
  const dependencies = {
    isTauri: () => true,
    registerAgentConversationStream: async () => {
      registering.resolve();
      if (holdRegistration) await registrationReply.promise;
      let active = true;
      return { unregister: () => {
        if (active) { active = false; unregisters++; }
        return unregister.promise;
      } };
    },
    handleConversationStreamEnvelope() {}, handleConversationStreamResync() {},
    registerAguiStream: async () => ({ unregister: async () => {} }), admitSelectedConversationChunks() {},
    listen: async (name: string, callback: Function) => {
      handlers.set(name, callback);
      if (name === holdListener) { listening.resolve(); await listenerReply.promise; }
      return () => handlers.delete(name);
    },
    trackTauriListener: (stop: () => void) => {
      listeners++;
      let active = true;
      return () => { if (active) { active = false; listeners--; stop(); } };
    },
    showNamedSession() { updates++; }, setRemoteConnection() { updates++; },
    readRemoteAssemblyEnvironmentFromTauri: () => { requested.resolve(); return environment.promise; },
    rail, refreshRemoteSessionActivity: async () => { if (holdInventory) await inventory.promise; },
    reacquireSelectedChildHistory: async () => { reloads++; },
    stopChildConversationHistory() {}, stopConversationTerminalProjection() {}
  };
  const api = Function(...Object.keys(dependencies), globals +
    ['startConversationEvents', 'setupConversationEvents', 'stopConversationEvents'].map(extract).join('\n') +
    '\nreturn { startConversationEvents, stopConversationEvents };')(...Object.values(dependencies));
  return { ...api, environment, requested, unregister, registering, registrationReply, handlers, rail, listening, listenerReply,
    inventory, reloads: () => reloads, listeners: () => listeners, unregisters: () => unregisters, updates: () => updates };
}

await test('reconnect reloads the selected transcript while remote inventory is still pending', async () => {
  const h = eventHarness(false, undefined, true);
  const setup = h.startConversationEvents();
  await h.requested.promise;
  h.environment.resolve({ profiles: [], readyProfileIds: [] });
  await setup;
  const reconnect = h.handlers.get('remote-connection-changed')!({ payload: { profileId: 'healthy', state: 'connected' } });
  assert.equal(h.reloads(), 1, 'unresponsive inventory must not delay the healthy transcript');
  h.inventory.resolve();
  await reconnect;
  h.stopConversationEvents();
  h.unregister.resolve();
});

await test('shutdown removes listeners before pending startup and unregister finish', async () => {
  const h = eventHarness();
  const setup = h.startConversationEvents();
  await h.requested.promise;
  assert.equal(h.listeners(), 2);
  const callback = h.handlers.get('remote-connection-changed')!;
  h.stopConversationEvents();
  assert.equal(h.listeners(), 0);
  assert.equal(h.unregisters(), 1);
  callback({ payload: { profileId: 'old', state: 'connected' } });
  assert.equal(h.updates(), 0, 'a queued callback cannot change state after shutdown');
  h.environment.resolve({ profiles: [{ id: 'old' }], readyProfileIds: ['old'] });
  await setup;
  assert.deepEqual(h.rail.remoteProfiles, [], 'late startup cannot seed stale state');
  h.unregister.resolve();
});

for (const name of ['session-title-changed', 'remote-connection-changed']) {
  await test(`shutdown cleans a late ${name} listener`, async () => {
    const h = eventHarness(false, name);
    const setup = h.startConversationEvents();
    await h.listening.promise;
    h.stopConversationEvents();
    assert.equal(h.listeners(), 0, 'previously acquired listeners stop immediately');
    h.listenerReply.resolve();
    await setup;
    assert.equal(h.listeners(), 0, 'the late registration also closes');
    assert.equal(h.unregisters(), 1);
    h.unregister.resolve();
  });
}

await test('registration completing after shutdown is closed without acquiring listeners', async () => {
  const h = eventHarness(true);
  const setup = h.startConversationEvents();
  await h.registering.promise;
  h.stopConversationEvents();
  h.registrationReply.resolve();
  await setup;
  assert.equal(h.unregisters(), 1);
  assert.equal(h.listeners(), 0);
  h.unregister.resolve();
});

await test('restart does not wait for a stopped startup or let it overwrite the new stream', async () => {
  const h = eventHarness();
  const first = h.startConversationEvents();
  await h.requested.promise;
  h.stopConversationEvents();
  const second = h.startConversationEvents();
  h.environment.resolve({ profiles: [], readyProfileIds: [] });
  h.unregister.resolve();
  await Promise.all([first, second]);
  assert.equal(h.listeners(), 2, 'only the replacement owns listeners');
  h.stopConversationEvents();
  assert.equal(h.listeners(), 0);
});

function selectionHarness() {
  const reading = deferred(), reply = deferred<any>();
  let starts = 0, snapshots = 0;
  const dependencies = {
    startConversationEvents: async () => { starts++; },
    attachHistoryDownload: async () => {}, clearConversationHistoryDownload() {},
    reloadSelectedConversation: async () => { reading.resolve(); await reply.promise; },
  };
  const api = Function(...Object.keys(dependencies), globals + extract('subscribeSelectedConversation') +
    '\nreturn { subscribeSelectedConversation, current: () => selectedConversationRead };')(...Object.values(dependencies));
  const input = (signal: AbortSignal) => ({ workspaceOwnedId: 'one', ownedId: 'one', maxBytes: 1024, signal,
    onSnapshot() { snapshots++; }, onEvent() {}, onError(error: unknown) { throw error; } });
  return { ...api, reading, reply, input, starts: () => starts, snapshots: () => snapshots };
}

await test('an already-aborted subscription does not start work or replace the current selection', async () => {
  const h = selectionHarness(), owner = new AbortController();
  const opening = h.subscribeSelectedConversation(h.input(owner.signal));
  await h.reading.promise;
  const current = h.current();
  const cancelled = new AbortController(); cancelled.abort();
  const dispose = await h.subscribeSelectedConversation(h.input(cancelled.signal));
  dispose();
  assert.equal(h.current(), current);
  assert.equal(h.starts(), 1);
  owner.abort(); h.reply.resolve(null); await opening;
});

await test('aborting a pending initial read releases the service reference immediately', async () => {
  const h = selectionHarness(), owner = new AbortController();
  const opening = h.subscribeSelectedConversation(h.input(owner.signal));
  await h.reading.promise;
  const read = h.current();
  read.events.push({ retained: true }); read.bytes = 100;
  owner.abort();
  assert.equal(h.current(), null);
  assert.deepEqual(read.events, []);
  assert.equal(read.bytes, 0);
  h.reply.resolve(null);
  const dispose = await opening;
  dispose(); dispose();
  assert.equal(h.current(), null);
});

await test('remote activity refresh cancels superseded reads and shutdown cancels the current one', async () => {
  const requests: Array<{ signal: AbortSignal; reply: ReturnType<typeof deferred<any>> }> = [];
  let updates = 0;
  const dependencies = {
    listRemoteAgentConversationSessionsFromTauri: (signal: AbortSignal) => {
      const reply = deferred<any>(); requests.push({ signal, reply }); return reply.promise;
    },
    rail: { remoteConnections: { workbox: 'connected' }, error: null },
    preparingSends: new Map(), updateOwnedSession() { updates++; },
    synchronizeSessionPresenceWork() {}, getConversationSession: () => null,
    stopChildConversationHistory() {}, stopConversationTerminalProjection() {}
  };
  const api = Function(...Object.keys(dependencies), globals +
    extract('refreshRemoteSessionActivity') + extract('stopConversationEvents') + `
    return { refresh: () => refreshRemoteSessionActivity(conversationEventsGeneration), stopConversationEvents };`)(...Object.values(dependencies));
  const first = api.refresh(), second = api.refresh();
  assert.equal(requests[0].signal?.aborted, true);
  assert.equal(requests[1].signal.aborted, false);
  api.stopConversationEvents();
  assert.equal(requests[1].signal.aborted, true);
  for (const request of requests) request.reply.resolve([{ ownedId: 'old', remoteProfileId: 'workbox' }]);
  await Promise.all([first, second]);
  assert.equal(updates, 0);
});

await test('a resync finishing after shutdown does not start another remote read', async () => {
  const reload = deferred(), reading = deferred();
  let requests = 0;
  const dependencies = {
    reloadSelectedConversation: async () => { reading.resolve(); await reload.promise; },
    isCurrentConversationRead: () => false,
    listRemoteAgentConversationSessionsFromTauri: async () => { requests++; return []; },
    stopChildConversationHistory() {}, stopConversationTerminalProjection() {}
  };
  const api = Function(...Object.keys(dependencies), globals +
    ['refreshRemoteSessionActivity', 'handleConversationStreamResync', 'stopConversationEvents'].map(extract).join('\n') + `
    selectedConversationRead = { abortController: new AbortController() };
    return { resync: () => handleConversationStreamResync(0), stopConversationEvents };`)(...Object.values(dependencies));
  const resync = api.resync();
  await reading.promise;
  api.stopConversationEvents(); reload.resolve(); await resync;
  assert.equal(requests, 0);
});

await test('a connection inherits an already-aborted subscription or selection signal', async () => {
  const connection = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const match = connection.match(/^function createConnection\([\s\S]*?^}$/m);
  assert.ok(match);
  let opens = 0;
  const dependencies = {
    enqueueFactory: (signal: AbortSignal) => ({
      push() {}, async *stream() { assert.equal(signal.aborted, true); }
    }),
    subscribeSelectedConversation: async () => { opens++; return () => {}; }
  };
  const createConnection = Function(...Object.keys(dependencies), stripTypeScriptTypes(match[0]) +
    '\nreturn createConnection;')(...Object.values(dependencies));
  for (const cancelled of ['subscription', 'selection']) {
    const owner = new AbortController(), selection = { controller: new AbortController() };
    if (cancelled === 'subscription') owner.abort(); else selection.controller.abort();
    const stream = createConnection(selection).subscribe(owner.signal);
    assert.equal((await stream.next()).done, true);
    assert.equal(selection.push, undefined);
    assert.equal(selection.resubscribe, undefined);
  }
  assert.equal(opens, 0);
});
