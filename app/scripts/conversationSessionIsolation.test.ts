import { test } from 'node:test';
import { normalizeProvider } from '../src/lib/shell/ownedSessions.ts';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';

import {
  captureWorkspace,
  normalizeWorkspaceSnapshot
} from '../src/lib/shell/sessionWorkspaces.ts';

function sessionSnapshot(name, mode, activeKey) {
  return captureWorkspace({
    openFiles: [{ path: `/repo/${name}.cs` }],
    activePath: `/repo/${name}.cs`,
    selectedPath: `/repo/${name}.cs`,
    scrollTop: name.length,
    diffPath: `${name}.cs`,
    diffRoot: '/repo',
    conversation: {
      mode,
      draft: `draft-${name}`,
      selectedChildId: `child-${name}`,
      providerGeneration: 2,
      lastSequence: 8,
      version: 1,
      generation: 2,
      owner: 'terminal',
      attachmentIds: [`attachment-${name}`],
      config: { future_category: name },
      viewByHistoryId: { [name]: { followLatest: false, anchor: { itemId: `item-${name}`, firstSequence: 1, offsetPx: 20 }, expandedTurns: {} } },
      sequence: 8,
      telemetry: { providerLatencyMs: name.length },
      futureWorkspaceField: { owner: name }
    },
    browser: {
      tabs: [{
        id: `browser-${name}`,
        url: `http://${name}.localhost:5177/`,
        inputUrl: `${name}.localhost:5177`,
        title: name
      }],
      activeTabId: `browser-${name}`
    },
    topTabs: {
      order: ['diff', `editor:/repo/${name}.cs`, `browser:browser-${name}`],
      activeKey
    }
  });
}

const sessions = {
  'owned-a': sessionSnapshot('alpha', 'structured', 'editor:/repo/alpha.cs'),
  'owned-b': sessionSnapshot('bravo', 'raw', 'browser:browser-bravo'),
  'owned-c': sessionSnapshot('charlie', 'structured', 'diff')
};

const restored = Object.fromEntries(
  Object.entries(sessions).map(([ownedId, snapshot]) => [
    ownedId,
    normalizeWorkspaceSnapshot(snapshot)!
  ])
);

assert.equal('draft' in restored['owned-a'].conversation, false);
assert.equal(restored['owned-b'].conversation.mode, 'raw');
assert.equal(restored['owned-a'].conversation.selectedChildId, 'child-alpha');
assert.equal(restored['owned-b'].conversation.viewByHistoryId.bravo.anchor.itemId, 'item-bravo');
assert.equal(restored['owned-c'].browser.tabs[0].url, 'http://charlie.localhost:5177/');
assert.equal(restored['owned-c'].browser.activeTabId, 'browser-charlie');
assert.equal(restored['owned-a'].topTabs.activeKey, 'editor:/repo/alpha.cs');
assert.equal(restored['owned-b'].topTabs.activeKey, 'browser:browser-bravo');
assert.equal(restored['owned-c'].topTabs.activeKey, 'diff');
assert.notDeepEqual(restored['owned-a'].topTabs.order, restored['owned-b'].topTabs.order);
assert.deepEqual(restored['owned-a'].conversation.attachmentIds, ['attachment-alpha']);
assert.equal(restored['owned-b'].conversation.config.future_category, 'bravo');
assert.equal(restored['owned-a'].conversation.futureWorkspaceField.owner, 'alpha');
assert.notDeepEqual(
  restored['owned-a'].conversation.viewByHistoryId,
  restored['owned-b'].conversation.viewByHistoryId
);

// A pre-conversation snapshot migrates without inventing another session's UI.
const legacy = normalizeWorkspaceSnapshot({
  openPaths: ['/repo/legacy.cs'],
  activePath: '/repo/legacy.cs',
  selectedPath: null,
  scrollTop: 0,
  diffPath: null,
  diffRoot: null
});
assert.ok(legacy);
assert.equal(legacy.conversation, undefined);
assert.equal(legacy.browser, undefined);
assert.equal(legacy.topTabs, undefined);

// Exercise the production fixed client slots with a transport-free chat fixture.
{
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const current = source.slice(source.indexOf('function isCurrent('), source.indexOf('function messageBytes('));
  const bind = source.slice(source.indexOf('function resetReady('), source.indexOf('async function restorePageAttachments('));
  const selection = source.slice(source.indexOf('export function selectConversationChat('), source.indexOf('export async function sendSelectedConversationMessage(')).replaceAll('export ', '');
  const states = new Map<string, { provider: string; generation: number; selectedChildHistoryOwnedId: string | null; draft?: string; selectedTurns?: unknown[] }>();
  const rail = { owned: [] as Array<{ ownedId: string; agent: string; remoteProfileId?: string }> };
  const clearedAttachments: string[] = [];
  const disposed: string[] = [];
  const closedViews: string[] = [];
  const dependencies = {
    rail,
    normalizeProvider,
    getConversationSession: (id: string) => states.get(id),
    ensureConversationSession: (id: string, provider: string) => {
      if (states.get(id)?.provider !== provider) states.set(id, { provider, generation: 1, selectedChildHistoryOwnedId: null });
      return states.get(id);
    },
    evictConversationSession: (id: string) => states.delete(id),
    restoreSelectedConversationAttachments: (id: string) => clearedAttachments.push(id),
    closeAguiViewInBackground: (id: string) => closedViews.push(id),
    rejectPendingAdmission: (value: { pendingAdmission: unknown }) => { value.pendingAdmission = null; },
    createConnection: () => ({}),
    createChat: ({ threadId }: { threadId: string }) => ({
      messages: [], dispose: () => disposed.push(threadId)
    })
  };
  const api = Function(...Object.keys(dependencies), stripTypeScriptTypes(`
    let active = null;
    let childActive = null;
    ${current}${bind}${selection}
  `, { mode: 'strip' }) + `
    return { selectConversationChat, selectedConversationChat, disposeChildConversationChat,
      disposeSelectedConversationChat, parent: () => active, child: () => childActive };
  `)(...Object.values(dependencies));
  dependencies.ensureConversationSession('parent-a', 'codex');
  const parentOwner = new AbortController();
  const parent = api.selectConversationChat('parent-a', 'parent-a', parentOwner.signal);
  const admission = { waiting: true };
  api.parent().pendingAdmission = admission;
  const childOwner = new AbortController();
  const child = api.selectConversationChat('parent-a', 'history-a', childOwner.signal);
  assert.equal(api.selectedConversationChat('parent-a'), parent);
  assert.equal(api.selectedConversationChat('parent-a', 'history-a'), child);
  assert.equal(api.selectConversationChat('parent-a', 'history-a', childOwner.signal), child);
  assert.deepEqual(disposed, []);
  api.disposeChildConversationChat('parent-a');
  assert.equal(api.selectedConversationChat('parent-a'), parent);
  assert.equal(api.parent().pendingAdmission, admission, 'Back preserves an admitted parent send');
  assert.equal(states.has('history-a'), false);
  assert.equal(states.get('parent-a')!.selectedChildHistoryOwnedId, null);
  assert.deepEqual(clearedAttachments, ['history-a']);
  const obsoleteOwner = new AbortController();
  api.selectConversationChat('parent-a', 'history-b', obsoleteOwner.signal);
  const next = api.selectConversationChat('parent-a', 'history-c');
  obsoleteOwner.abort();
  assert.equal(api.selectedConversationChat('parent-a', 'history-c'), next, 'obsolete owner cannot dispose the replacement');
  assert.equal(states.has('history-b'), false);
  const parentSelection = api.parent();
  parentOwner.abort();
  assert.equal(parentSelection.pendingAdmission, admission,
    'leaving a session after Send does not un-send it: the send settles its own admission');
  assert.equal(api.selectedConversationChat('parent-a'), null);
  assert.equal(api.selectedConversationChat('parent-a', 'history-c'), null);
  assert.equal(states.has('history-c'), false);
  assert.deepEqual(disposed, ['history-a', 'history-b', 'history-c', 'parent-a']);
  assert.deepEqual(closedViews, disposed, 'a disposed chat stops its backend view');

  const panelSource = readFileSync(new URL('../src/lib/shell/panels/agents/AgentsPanel.svelte', import.meta.url), 'utf8');
  const panelSelect = panelSource.slice(panelSource.indexOf('  async function select('), panelSource.indexOf('  function openFile('));
  const serviceSource = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const reconnect = serviceSource.slice(serviceSource.indexOf('async function reacquireSelectedChildHistory('), serviceSource.indexOf('function handleConversationStreamEnvelope('));
  const readers = serviceSource.slice(serviceSource.indexOf('function selectedEventUsesControlCursor('), serviceSource.indexOf('export async function readOlderSelectedConversationItems(')).replaceAll('export ', '');

  for (const parentProvider of ['claude', 'codex']) {
    for (const provider of ['claude', 'codex']) {
      for (const remote of [false, true]) {
        const label = `${parentProvider} parent / ${provider} helper / ${remote ? 'remote' : 'local'}`;
        const parentId = `parent-${label}`;
        const helperId = `helper-${label}`;
        const profileId = remote ? 'workbox-test' : undefined;
        rail.owned = [
          { ownedId: parentId, agent: parentProvider, remoteProfileId: profileId },
          { ownedId: helperId, agent: provider, remoteProfileId: profileId }
        ];
        const parentState = dependencies.ensureConversationSession(parentId, parentProvider)!;
        Object.assign(parentState, {
          selectedChildId: null,
          children: [{ childId: helperId, parentToolCallId: '', transcriptAvailable: true }]
        });
        const parentChat = api.selectConversationChat(parentId);
        const imported: string[] = [];
        const navigated: string[] = [];
        const panelDependencies = {
          getConversationSession: dependencies.getConversationSession,
          adoptSession: async (id: string, signal: AbortSignal) => !signal.aborted && rail.owned.some((row) => row.ownedId === id),
          setConversationSelectedChild: (_id: string, childId: string | null) => { Object.assign(parentState, { selectedChildId: childId }); },
          selectConversationChat: api.selectConversationChat,
          selectedConversationChatReady: async () => {},
          readChildConversationHistory: async ({ childSessionId }: { childSessionId: string }) => {
            imported.push(childSessionId);
            return { historyOwnedId: 'native-history' };
          },
          sessionRowJump: async (id: string) => { navigated.push(id); }
        };
        const panel = Function(...Object.keys(panelDependencies), stripTypeScriptTypes(`
          let ownedId = ${JSON.stringify(parentId)};
          let conversation = getConversationSession(ownedId);
          let visible = true, view = 'session', openedChildId = null;
          let reader = null;
          let transcriptState = 'unavailable', transcriptError = '';
          function release(parentId) {
            reader?.abort();
            setConversationSelectedChild(parentId, null);
          }
          ${panelSelect}
        `, { mode: 'strip' }) + `return { select, status: () => transcriptState, signal: () => reader.signal, back: () => release(ownedId) };`)(...Object.values(panelDependencies));
        await panel.select(helperId);
        assert.equal(api.selectedConversationChat(parentId), parentChat, label);
        assert.deepEqual(navigated, [], `helper child opens in the child reader: ${label}`);
        assert.deepEqual(imported, [], label);
        assert.equal(panel.status(), 'ready', label);
        assert.ok(api.selectedConversationChat(parentId, helperId), label);
        assert.equal(states.get(helperId)?.provider, provider, `helper uses its own provider: ${label}`);

        const helperState = states.get(helperId)!;
        const pendingSend = { owner: helperId };
        Object.assign(helperState, { draft: 'unfinished', sending: true, selectedTurns: [{ turnId: 'displayed-turn' }] });
        api.child().pendingSend = pendingSend;
        const snapshots: string[] = [];
        const displayed: number[] = [];
        const serviceDependencies = {
          rail,
          getConversationSession: dependencies.getConversationSession,
          readChildConversationHistory: panelDependencies.readChildConversationHistory,
          startConversationEvents: async () => {},
          clearConversationHistoryDownload: () => {},
          attachHistoryDownload: async () => {},
          readAgentConversationSelectionFromTauri: async (id: string) => {
            snapshots.push(id);
            return { connection: { generation: 1 }, page: { watermark: 1 }, pendingSequence: 1 };
          },
          ACTIVE_EVENT_WINDOW_EVENTS: 20000,
          ACTIVE_EVENT_WINDOW_BYTES: 4 * 1024 * 1024
        };
        const service = Function(...Object.keys(serviceDependencies), stripTypeScriptTypes(`
          let selectedConversationRead = null, childConversationRead = null;
          let conversationEventsDisposed = false, conversationEventsGeneration = 1;
          function isCurrentConversationRead(read) {
            return selectedConversationRead === read || childConversationRead === read;
          }
          ${readers}${reconnect}
        `, { mode: 'strip' }) + `return { subscribeSelectedConversation, admitSelectedConversationEvent,
            reconnect: reacquireSelectedChildHistory };`)(...Object.values(serviceDependencies));
        const stop = await service.subscribeSelectedConversation({
          workspaceOwnedId: parentId, ownedId: helperId, maxBytes: 512 * 1024, signal: panel.signal(),
          onSnapshot: () => { displayed.length = 0; displayed.push(1); },
          onEvent: (event: { sequence: number }) => displayed.push(event.sequence),
          onError: (error: unknown) => { throw error; }
        });
        const event = { ownedId: helperId, generation: 1, sequence: 2, payload: { kind: 'message' } };
        service.admitSelectedConversationEvent(event);
        service.admitSelectedConversationEvent(event);
        assert.deepEqual(displayed, [1, 2], `helper child stays live: ${label}`);
        if (remote) {
          await service.reconnect(profileId, 1);
          assert.deepEqual(snapshots, [helperId, helperId], `helper child reconnect reloads by owned id: ${label}`);
          assert.deepEqual(imported, [], label);
        }
        const beforeAbort = [...displayed];
        const childSelection = api.child();
        panel.back();
        service.admitSelectedConversationEvent({ ...event, sequence: 3 });
        stop();
        api.disposeChildConversationChat(parentId);
        assert.deepEqual(displayed, beforeAbort, `late updates stop: ${label}`);
        assert.equal(api.selectedConversationChat(parentId, helperId), null, label);
        assert.equal(states.get(helperId), helperState, `Back preserves helper state: ${label}`);
        assert.equal(states.get(helperId)?.draft, 'unfinished', label);
        assert.equal(childSelection.pendingSend, pendingSend, label);
        assert.deepEqual(helperState.selectedTurns, [], `Back releases displayed facts: ${label}`);
        if (remote) {
          Object.assign(parentState, { selectedChildId: 'native-child', children: [{ childId: 'native-child', parentToolCallId: 'tool', transcriptAvailable: true, transcriptId: 'native-transcript' }] });
          const nativeOwner = new AbortController();
          const stopNative = await service.subscribeSelectedConversation({
            workspaceOwnedId: parentId, ownedId: 'native-history', maxBytes: 512 * 1024,
            signal: nativeOwner.signal, onSnapshot: () => {}, onEvent: () => {},
            onError: (error: unknown) => { throw error; }
          });
          await service.reconnect(profileId, 1);
          assert.deepEqual(imported, ['native-transcript'], 'native child reconnect uses transcriptId');
          assert.deepEqual(snapshots.slice(-2), ['native-history', 'native-history'], 'native reader reloads after import');
          nativeOwner.abort();
          stopNative();
        }
        api.disposeSelectedConversationChat(parentId);
        console.log(`helper child selection, live release, and reconnect passed: ${label}`);
      }
    }
  }

  await test('a selected antigravity helper keeps its own provider', () => {
    dependencies.ensureConversationSession('alias-parent', 'claude');
    for (const agent of ['antigravity', 'gemini', 'agy', 'anthropic', 'openai', 'claudecode']) {
      const id = `helper-${agent}`;
      rail.owned.push({ ownedId: id, agent });
      api.selectConversationChat('alias-parent', id);
      assert.equal(states.get(id)?.provider, { antigravity: 'antigravity', gemini: 'claude', agy: 'antigravity', anthropic: 'claude', openai: 'codex', claudecode: 'claude' }[agent]);
      api.disposeChildConversationChat('alias-parent');
    }
  });
}

console.log('conversation session isolation tests passed');
