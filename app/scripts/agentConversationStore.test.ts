/// <reference types="node" />

import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { compileModule } from 'svelte/compiler';
import { get } from 'svelte/store';
import { normalizeProvider } from '../src/lib/shell/ownedSessions.ts';
import { EventType, StreamProcessor, type StreamChunk } from '@tanstack/ai/client';
import { shouldClearConversationSending } from '../src/lib/shell/conversation/conversationReducer.ts';
import { conversationDisplayItems } from '../src/lib/shell/conversation/conversationMessages.ts';
import { agentItemFromEvent, displayItemFromAgentItem } from '../src/lib/shell/conversation/conversationTimeline.ts';

import { sessionPresenceHistory, sessionPresenceEventFromConversation, synchronizeSessionPresenceWork, deriveSessionPresence, EMPTY_SESSION_PRESENCE_HISTORY } from '../src/lib/shell/conversation/sessionPresence.ts';

type ProviderName = 'codex' | 'claude';

interface AgentConversationEvent<TPayload = Record<string, unknown>> {
  ownedId: string;
  provider: ProviderName;
  generation: number;
  sequence: number;
  timestampMs: number;
  payload: TPayload;
}

const storePath = fileURLToPath(
  new URL('../src/lib/shell/conversation/conversationStore.svelte.ts', import.meta.url)
);
const outputPath = fileURLToPath(
  new URL('../src/lib/shell/conversation/.conversationStore.test.mjs', import.meta.url)
);
// Compile the resource-counter runes module and point the store at that copy.
const diagnosticsPath = fileURLToPath(
  new URL('../src/lib/shell/resourceDiagnostics.svelte.ts', import.meta.url)
);
const diagnosticsOutputPath = fileURLToPath(
  new URL('../src/lib/shell/.resourceDiagnostics.test.mjs', import.meta.url)
);

// Node has no bundler, so the build-time flag both modules read is compiled
// out as false, which is what the shipped app sees.
function compileForTest(modulePath: string, filename: string): string {
  const stripped = stripTypeScriptTypes(readFileSync(modulePath, 'utf8'), { mode: 'strip' });
  const module = compileModule(stripped, { generate: 'client', filename });
  return module.js.code.replaceAll('import.meta.env', '({ DEV: false })');
}

writeFileSync(
  outputPath,
  compileForTest(storePath, 'conversationStore.svelte.js')
    .replace(/\.\.\/resourceDiagnostics\.svelte(\.ts|\.js)/, '../.resourceDiagnostics.test.mjs')
);
writeFileSync(
  diagnosticsOutputPath,
  compileForTest(diagnosticsPath, 'resourceDiagnostics.svelte.js')
);

globalThis.$state = (value) => value;
export let store;
try {
  store = await import(`${outputPath}?test=${Date.now()}`);
} finally {
  rmSync(outputPath, { force: true });
  rmSync(diagnosticsOutputPath, { force: true });
}

const a = store.ensureConversationSession('owned-a', 'codex');
const b = store.ensureConversationSession('owned-b', 'claude');
assert.notEqual(a, b);

store.setConversationDraft('owned-a', 'Message A');
store.setConversationDraft('owned-b', 'Message B');
store.setConversationMode('owned-b', 'raw');
assert.equal(store.getConversationSession('owned-a').draft, 'Message A');
assert.equal(store.getConversationSession('owned-a').mode, 'structured');
assert.equal(store.getConversationSession('owned-b').draft, 'Message B');
assert.equal(store.getConversationSession('owned-b').mode, 'raw');


store.applySelectedConversationSnapshotState('owned-a', 'owned-a', {
  connection: {
    ownedId: 'owned-a', provider: 'codex', generation: 1,
    state: 'connected', nativeSessionId: 'thread-a'
  },
  suspended: false,
  page: {
    items: [], events: [], turns: [], hasBefore: false, hasEarlierTranscript: false, hasAfter: false,
    watermark: 0, transferBytes: 0, oversized: false
  },
  pendingEvents: [{
    ownedId: 'owned-a', provider: 'codex', generation: 1, sequence: 10, timestampMs: 1,
    payload: {
      kind: 'childUpdate', childId: 'restored-child', parentToolCallId: 'tool-parent',
      transcriptId: 'durable-restored-child', label: 'Restored child', state: 'completed'
    }
  }], pendingSequence: 10
});
assert.equal(store.getConversationSession('owned-a').children[0]?.childId, 'restored-child',
  'authoritative child metadata restores outside the bounded content page');
store.setConversationAttachments('owned-a', [{
  id: 'image-a', name: 'a.png', mimeType: 'image/png', path: '/managed/a.png', previewUrl: 'blob:a'
}]);
store.setConversationSelectedChild('owned-a', 'child-a');
store.ensureConversationSession('opaque-child-history', 'claude');
store.applySelectedConversationSnapshotState('opaque-child-history', 'opaque-child-history', {
  connection: { ownedId: 'opaque-child-history', provider: 'claude', generation: 9, state: 'disconnected' },
  suspended: false,
  activeTurnId: 'child-turn',
  page: {
    items: [], events: [], turns: [{ turnId: 'child-turn', terminalState: 'completed' }],
    hasBefore: true, hasEarlierTranscript: true, hasAfter: false,
    watermark: 20, transferBytes: 40, oversized: false
  },
  pendingEvents: [], pendingSequence: 20
}, false);
assert.equal(store.getConversationSession('owned-a').generation, 1, 'child snapshots preserve parent generation');
assert.equal(store.getConversationSession('owned-a').connectionState, 'connected', 'child snapshots preserve parent controls');
assert.equal(store.getConversationSession('owned-a').selectedHistoryOwnedId, 'owned-a');
assert.equal(store.getConversationSession('opaque-child-history').selectedHistoryOwnedId, 'opaque-child-history');
assert.equal(store.getConversationSession('opaque-child-history').selectedHasBefore, true);
store.applySelectedConversationEventState('owned-a', {
  ownedId: 'owned-a', provider: 'codex', generation: 2, sequence: 11, timestampMs: 2,
  payload: { kind: 'connection', state: 'connecting' }
});
assert.equal(store.getConversationSession('owned-a').generation, 2);
assert.equal(store.getConversationSession('owned-a').desynchronized, false, 'journal sequence continues across runtime generations');
assert.equal(store.getConversationSession('owned-a').connectionState, 'connecting');
store.applySelectedConversationEventState('owned-a', {
  ownedId: 'owned-a', provider: 'codex', generation: 2, sequence: 11, timestampMs: 4,
  payload: {
    kind: 'childUpdate', childId: 'restored-child', parentToolCallId: 'tool-parent',
    transcriptId: 'durable-restored-child', label: 'Restored child', state: 'running'
  }
});
assert.equal(store.getConversationSession('owned-a').children[0]?.state, 'completed',
  'stale child updates cannot overwrite authoritative snapshot controls');
assert.deepEqual(store.getConversationSession('opaque-child-history').selectedTurns, [{
  turnId: 'child-turn', terminalState: 'completed'
}], 'parent controls do not enter child turn facts');
assert.equal('transcript' in store.getConversationSession('owned-a'), false);
assert.equal('childTranscript' in store.getConversationSession('owned-a'), false);
assert.equal('loadedEvents' in store.getConversationSession('owned-a'), false);

store.setConversationConnection({
  ownedId: 'owned-b', provider: 'claude', generation: 2, state: 'connecting'
});
assert.equal(store.setConversationWriterLeaseTransition({
  ownedId: 'owned-b', generation: 2, from: 'none', to: 'terminal', state: 'committed'
}), true);
assert.equal(store.getConversationSession('owned-b').executionOwner, 'terminal');

store.setConversationCapabilities('owned-a', 2, {
  revision: 1,
  provider: 'codex',
  implementation: { name: 'fixture', version: '1' },
  session: { list: true, load: true, resume: true, close: true, steering: true },
  prompt: { text: true, image: false, embeddedContext: true, resourceLinks: true },
  interaction: {
    permissions: true, structuredUserInput: true, toolTerminals: true,
    plans: true, tasks: true, subagents: true
  },
  configOptions: [],
  commands: []
});
assert.equal(store.getConversationSession('owned-a').capabilitiesGeneration, 2);

const saved = store.captureConversationWorkspace('owned-b');
store.setConversationDraft('owned-b', 'Changed');
store.setConversationMode('owned-b', 'structured');
store.restoreConversationWorkspace('owned-b', 'claude', saved);
assert.equal(store.getConversationSession('owned-b').draft, 'Changed');
assert.equal(store.getConversationSession('owned-b').mode, 'raw');
assert.equal(store.getConversationSession('owned-b').selectedChildId, null);
assert.equal(saved.version, 1);
assert.equal(saved.owner, 'terminal');
assert.equal(saved.generation, 2);
assert.equal(saved.writerLease.owner, 'terminal');

// Sent screenshots stay keyed to the admitted user item outside the message graph.
{
  const ownedId = 'owned-sent';
  store.applySelectedConversationSnapshotState(ownedId, ownedId, {
    connection: { ownedId, provider: 'claude', generation: 1, state: 'connected' },
    suspended: false,
    page: {
      items: [], events: [], turns: [], hasBefore: false, hasEarlierTranscript: false,
      hasAfter: false, watermark: 0, transferBytes: 0, oversized: false
    },
    pendingEvents: [], pendingSequence: 0
  });
  store.recordSentConversationAttachments(ownedId, [{
    id: 'image-sent', name: 'shot.png', mimeType: 'image/png',
    path: '/managed/shot.png', previewUrl: 'blob:sent'
  }]);
  store.applySelectedConversationEventState(ownedId, {
    ownedId, provider: 'claude', generation: 1, sequence: 1, timestampMs: 500,
    payload: { kind: 'userMessage', itemId: 'user-turn-sent', text: 'Look', completed: true, attachmentIds: ['image-sent'] }
  });
  assert.equal(store.getConversationSession(ownedId).sentAttachments['user-turn-sent'][0].id, 'image-sent');
  assert.equal('transcript' in store.getConversationSession(ownedId), false);
}

test('attachment previews are revoked when replaced, but not while shown in a sent message', () => {
  const revoked: string[] = [];
  const originalRevoke = URL.revokeObjectURL;
  URL.revokeObjectURL = (url) => { revoked.push(url); };
  try {
    const ownedId = 'owned-preview-lifetime';
    store.ensureConversationSession(ownedId, 'claude');
    const attachment = (id: string, previewUrl: string) => ({
      id, name: `${id}.png`, mimeType: 'image/png', path: `/managed/${id}.png`, previewUrl
    });
    store.setConversationAttachments(ownedId, [attachment('draft', 'blob:draft')]);
    store.setConversationAttachments(ownedId, [attachment('replacement', 'blob:replacement')]);
    assert.deepEqual(revoked, ['blob:draft']);

    store.recordSentConversationAttachments(ownedId, [attachment('replacement', 'blob:replacement')]);
    store.setConversationAttachments(ownedId, []);
    assert.deepEqual(revoked, ['blob:draft']);
    store.setConversationConnection({ ownedId, provider: 'claude', generation: 1, state: 'connected' });
    store.applySelectedConversationEventState(ownedId, {
      ownedId, provider: 'claude', generation: 1, sequence: 1, timestampMs: 1,
      payload: { kind: 'userMessage', itemId: 'sent', text: 'See image', completed: true, attachmentIds: ['replacement'] }
    });
    assert.equal(store.getConversationSession(ownedId).sentAttachments.sent[0].previewUrl, 'blob:replacement');

    store.setConversationAttachments(ownedId, [attachment('replacement', 'blob:replacement')]);
    store.restoreSelectedConversationAttachments(ownedId, {}, 1, []);
    assert.deepEqual(store.getConversationSession(ownedId).sentAttachments, {});
    assert.deepEqual(revoked, ['blob:draft'], 'graph disposal preserves a matching draft preview');
    store.setConversationAttachments(ownedId, []);
    assert.deepEqual(revoked, ['blob:draft', 'blob:replacement']);
    store.evictConversationSession(ownedId);
    assert.deepEqual(revoked, ['blob:draft', 'blob:replacement']);
  } finally {
    URL.revokeObjectURL = originalRevoke;
  }
});

test('late attachment reads retain the current page and revoke evicted previews', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('async function restorePageAttachments('), source.indexOf('function pageAttachments('));
  const ownedId = 'owned-attachment-page-race';
  store.ensureConversationSession(ownedId, 'codex');
  store.setConversationConnection({ ownedId, provider: 'codex', generation: 1, state: 'connected' });
  const attachment = (id: string) => ({ id, name: `${id}.png`, mimeType: 'image/png', path: `/managed/${id}.png`, previewUrl: `blob:${id}` });
  const selection = {
    token: Symbol(), workspaceOwnedId: ownedId, historyOwnedId: ownedId,
    controller: new AbortController(), chat: { messages: [{ id: 'page-a' }] }
  };
  let release!: (value: unknown[]) => void;
  const pendingRead = new Promise<unknown[]>((resolve) => { release = resolve; });
  const restore = Function(
    'isCurrent', 'readSelectedConversationAttachments', 'restoreAttachmentList',
    'restoreSelectedConversationAttachments', 'discardRestoredAttachments', 'setConversationAttachmentError',
    `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn restorePageAttachments;`
  )((candidate: unknown) => candidate === selection && !selection.controller.signal.aborted, () => pendingRead, async (unused: string, records: unknown[]) => records,
    store.restoreSelectedConversationAttachments, () => undefined,
    (unused: string, message: string) => { throw new Error(message); });
  const revoked: string[] = [];
  const originalRevoke = URL.revokeObjectURL;
  URL.revokeObjectURL = (url) => { revoked.push(url); };
  try {
    store.restoreSelectedConversationAttachments(ownedId, { stale: [attachment('stale')] }, 1, ['stale']);
    const loading = restore(selection, new Map([['page-a', ['a']]]), 1, ['page-a']);
    assert.deepEqual(revoked, ['blob:stale'], 'replacement prunes before waiting on previews');
    selection.chat.messages = [{ id: 'page-b' }];
    store.restoreSelectedConversationAttachments(ownedId, { 'page-b': [attachment('b')] }, 1, ['page-b']);
    release([attachment('a')]);
    await loading;
    assert.deepEqual(Object.keys(store.getConversationSession(ownedId).sentAttachments), ['page-b']);
    assert.deepEqual(revoked, ['blob:stale', 'blob:a'], 'late evicted preview is released while page B remains');
  } finally {
    store.evictConversationSession(ownedId);
    URL.revokeObjectURL = originalRevoke;
  }
});

test('message sizes are exact and reuse parts already measured', () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('function isCurrent('), source.indexOf('function resetMessageBytes('));
  const encoder = new TextEncoder();
  let stringified: unknown[] = [];
  const countingJson = { stringify: (value: unknown) => { stringified.push(value); return JSON.stringify(value); } };
  const messageBytes = Function('encoder', 'JSON', `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn messageBytes;`)(encoder, countingJson);
  const messages = [
    { id: 'empty', role: 'assistant', parts: [], metadata: { turnId: 'turn-1' } },
    { id: 'one', role: 'user', parts: [{ type: 'text', content: 'Look at this — café ✓ 漢字 🙂' }] },
    { id: 'many', role: 'assistant', metadata: { firstSequence: 4 }, parts: [
      { type: 'thinking', content: 'Plan' },
      { type: 'tool-call', id: 'call-1', name: 'Read', arguments: '{"path":"/a"}', state: 'input-complete', output: { text: 'ü' } },
      { type: 'text', content: 'Done.' }
    ] }
  ];
  for (const message of messages) {
    assert.equal(messageBytes(message), encoder.encode(JSON.stringify(message)).byteLength, message.id);
  }
  // A history snapshot hands back copied messages that keep the same part objects.
  stringified = [];
  const copies = messages.map((message) => ({ ...message, parts: [...message.parts] }));
  for (const copy of copies) messageBytes(copy);
  const parts = new Set<unknown>(messages.flatMap((message) => message.parts));
  const reserialized = stringified.filter((value) => parts.has(value)
    || ((value as { parts?: unknown[] }).parts ?? []).some((part) => parts.has(part)));
  assert.equal(reserialized.length, 0, 'known parts are not serialized again');
});

test('a stored message that names one attachment twice shows it once', async () => {
  // Messages saved before late September 2026 can list the same screenshot id
  // twice. The user card keys its screenshots by id, so a repeat stops it rendering.
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('async function restorePageAttachments('), source.indexOf('function pageAttachments('));
  const ownedId = 'owned-attachment-repeat';
  store.ensureConversationSession(ownedId, 'claude');
  store.setConversationConnection({ ownedId, provider: 'claude', generation: 1, state: 'connected' });
  const attachment = (id: string) => ({ id, name: `${id}.png`, mimeType: 'image/png', path: `/managed/${id}.png`, previewUrl: `asset://${id}` });
  const selection = { workspaceOwnedId: ownedId, historyOwnedId: ownedId, controller: new AbortController(), chat: { messages: [{ id: 'user-repeat' }] } };
  const restore = Function(
    'isCurrent', 'readSelectedConversationAttachments', 'restoreAttachmentList',
    'restoreSelectedConversationAttachments', 'discardRestoredAttachments', 'setConversationAttachmentError',
    `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn restorePageAttachments;`
  )(() => true, async () => [attachment('a'), attachment('b')], async (unused: string, records: unknown[]) => records,
    store.restoreSelectedConversationAttachments, () => undefined,
    (unused: string, message: string) => { throw new Error(message); });
  try {
    await restore(selection, new Map([['user-repeat', ['a', 'a', 'b']]]), 1, ['user-repeat']);
    assert.deepEqual(
      store.getConversationSession(ownedId).sentAttachments['user-repeat'].map((item: { id: string }) => item.id),
      ['a', 'b']
    );
  } finally {
    store.evictConversationSession(ownedId);
  }
});

test('selected delivery reloads gaps and suppresses controls already in the snapshot', () => {
  const source = readFileSync(
    new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url),
    'utf8'
  );
  const block = source.slice(
    source.indexOf('function selectedEventUsesControlCursor('),
    source.indexOf('function requestSelectedConversationReload(')
  );
  const delivered: number[] = [];
  let reloads = 0;
  const read = {
    ownedId: 'owned-gap', generation: 1, pageWatermark: 10, pendingSequence: 15,
    onEvent: (event: AgentConversationEvent) => delivered.push(event.sequence)
  };
  const deliver = Function(
    'requestSelectedConversationReload',
    `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn deliverSelectedConversationEvent;`
  )(() => { reloads += 1; }) as (state: typeof read, event: AgentConversationEvent) => void;

  deliver(read, {
    ownedId: read.ownedId, provider: 'codex', generation: 1,
    sequence: 11, timestampMs: 11,
    payload: { kind: 'turn', turnId: 'already-snapshotted', state: 'started' }
  });
  assert.equal(read.pageWatermark, 11);
  assert.equal(read.pendingSequence, 15);
  assert.deepEqual(delivered, []);

  deliver(read, {
    ownedId: read.ownedId, provider: 'codex', generation: 1,
    sequence: 12, timestampMs: 12,
    payload: {
      kind: 'childUpdate', childId: 'child', parentToolCallId: 'tool',
      state: 'running'
    }
  });
  assert.equal(read.pageWatermark, 12);
  assert.deepEqual(delivered, [], 'replay cannot regress an authoritative child descriptor');

  deliver(read, {
    ownedId: read.ownedId, provider: 'codex', generation: 1,
    sequence: 16, timestampMs: 16,
    payload: { kind: 'assistantDelta', itemId: 'answer', delta: 'late' }
  });
  assert.equal(reloads, 1);
  assert.equal(read.pageWatermark, 12);
  assert.deepEqual(delivered, []);
});

test('live turn facts stay stable and prune with the retained graph', () => {
  const ownedId = 'owned-turn-facts';
  store.applySelectedConversationSnapshotState(ownedId, ownedId, {
    connection: { ownedId, provider: 'codex', generation: 1, state: 'connected' },
    suspended: false,
    page: {
      items: [], events: [], turns: [], hasBefore: false, hasEarlierTranscript: false,
      hasAfter: false, watermark: 0, transferBytes: 0, oversized: false
    },
    pendingEvents: [], pendingSequence: 0
  });
  for (const event of [{
    ownedId, provider: 'codex', generation: 1, sequence: 1, timestampMs: 100,
    payload: { kind: 'turn', turnId: 'turn-1', state: 'started' }
  }, {
    ownedId, provider: 'codex', generation: 1, sequence: 2, timestampMs: 200,
    payload: { kind: 'turn', turnId: 'turn-1', state: 'completed' }
  }] as AgentConversationEvent[]) store.applySelectedConversationEventState(ownedId, event);
  assert.deepEqual(store.getConversationSession(ownedId).selectedTurns, [{
    turnId: 'turn-1', startedAtMs: 100, endedAtMs: 200, terminalState: 'completed'
  }]);
  store.applySelectedConversationLiveWindow(ownedId, [], 0);
  assert.deepEqual(store.getConversationSession(ownedId).selectedTurns, []);
  assert.equal(store.getConversationSession(ownedId).selectedHasBefore, false);
});

test('history gaps withhold live content and admitted messages stay retained', async () => {
  const source = readFileSync(
    new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url),
    'utf8'
  );
  const emitBlock = source.slice(
    source.indexOf('function emitEvent('),
    source.indexOf('function rejectPendingAdmission(')
  );
  const state = { selectedHasAfter: true, usage: undefined };
  let stateUpdates = 0;
  const { emitEvent: emit, emitChunks } = Function(
    'getConversationSession', 'applySelectedConversationEventState',
    'applySelectedConversationHistoryEventState', 'failChildConversationTranscript', 'setConversationSendError',
    `${stripTypeScriptTypes(emitBlock, { mode: 'strip' })}\nreturn { emitEvent, emitChunks };`
  )(
    () => state,
    () => { stateUpdates += 1; },
    () => { stateUpdates += 1; },
    () => undefined,
    () => undefined
  );
  const pushed: unknown[] = [];
  const event: AgentConversationEvent = {
    ownedId: 'owned-window', provider: 'codex', generation: 1,
    sequence: 2, timestampMs: 2,
    payload: { kind: 'assistantDelta', itemId: 'answer', delta: 'new' }
  };
  const selectionShown = { workspaceOwnedId: 'owned-window', historyOwnedId: 'owned-window', contentAdmitted: true };
  emit(selectionShown, event);
  assert.equal(stateUpdates, 1, 'native controls still advance');
  emitChunks(selectionShown, [
    { type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'answer', delta: 'new' },
    { type: EventType.RUN_FINISHED, threadId: 'owned-window', runId: 'run-1' }
  ], (item: unknown) => pushed.push(item));
  assert.deepEqual(pushed.map((chunk: any) => chunk.type), [EventType.RUN_FINISHED],
    'older content is not appended to the retained window, but the run still settles');

  const merge = Function(`${stripTypeScriptTypes(source.slice(
    source.indexOf('function mergeMessages('), source.indexOf('function recordLiveMessages(')
  ), { mode: 'strip' })}\nreturn mergeMessages;`)();
  const large = Array.from({ length: 300 }, (_, index) => ({
    id: `message-${index}`, role: 'assistant', parts: [{ type: 'text', content: 'x'.repeat(16 * 1024) }],
    metadata: { firstSequence: index + 1, turnId: `turn-${index}` }
  }));
  assert.ok(new TextEncoder().encode(JSON.stringify(large)).byteLength > 4 * 1024 * 1024);
  assert.deepEqual(merge(large.slice(150), large.slice(0, 151), 'older'), large);
  assert.deepEqual(merge(large.slice(0, 151), large.slice(150), 'newer'), large);
  const changed = { ...large[150], parts: [{ type: 'text', content: 'updated' }] };
  assert.equal(merge([changed], [large[150]], 'older')[0], changed);
  assert.equal(merge([large[150]], [changed], 'newer')[0], changed);

  let encodes = 0;
  const bytes = (message: unknown) => { encodes += 1; return new TextEncoder().encode(JSON.stringify(message)).byteLength; };
  const recordLive = Function(
    'messageBytes', 'applySelectedConversationLiveWindow', 'historyPosition', 'resetMessageBytes',
    `${stripTypeScriptTypes(source.slice(source.indexOf('function recordLiveMessages('),
      source.indexOf('function optimisticUserId(')), { mode: 'strip' })}\nreturn recordLiveMessages;`
  )(bytes, () => undefined, (message: any) => message?.metadata?.firstSequence, () => undefined);
  const sizes = new Map(large.map((row) => [row.id, bytes(row)]));
  const liveSelection = {
    chat: { messages: large }, messageBytes: sizes,
    graphBytes: [...sizes.values()].reduce((sum, size) => sum + size, 0), historyOwnedId: 'owned-window', beforeCursor: 1
  };
  encodes = 0;
  recordLive(liveSelection, new Set([large[299].id]), false);
  assert.equal(encodes, 1, 'one changed live message is remeasured without rescanning the graph');
  assert.equal(liveSelection.chat.messages, large);
  assert.ok(liveSelection.graphBytes > 4 * 1024 * 1024);
  assert.equal(liveSelection.beforeCursor, 1);

  let snapshotWindow: any;
  let snapshotTurns: any;
  let snapshotMessages: any;
  const oldTurn = { turnId: 'turn-0', startedAtMs: 1 };
  const finishedTurn = { turnId: 'turn-299', endedAtMs: 3 };
  const snapshotBlock = source.slice(source.indexOf('function snapshotChunks('), source.indexOf('function admitInitialSnapshot('));
  const admitSnapshot = Function(
    'messagesFromPage', 'getConversationSession', 'historyPosition', 'hasOlderHistory', 'resetMessageBytes',
    'applySelectedConversationSnapshotState', 'pushMessagesSnapshot', 'EventType', 'restorePageAttachments', 'pageAttachments',
    `${stripTypeScriptTypes(snapshotBlock, { mode: 'strip' })}\nreturn snapshotChunks;`
  )((page: any) => page.items, () => ({ selectedHasBefore: false, selectedTurns: [oldTurn, { turnId: 'turn-299' }] }),
    (message: any) => message?.metadata?.firstSequence, (page: any) => page.hasBefore,
    (selection: any, rows: any[]) => { selection.graphBytes = rows.reduce((sum, row) => sum + bytes(row), 0); },
    (_workspace: string, _history: string, snapshot: any, _controls: boolean, window: unknown) => {
      snapshotWindow = window; snapshotTurns = snapshot.page.turns;
    }, (_selection: unknown, rows: unknown) => { snapshotMessages = rows; }, EventType, () => undefined, () => []);
  const snapshotSelection: any = { historyOwnedId: 'owned-window', chat: { messages: large } };
  admitSnapshot(snapshotSelection, { page: { items: large.slice(250), beforeCursor: 251, afterCursor: 300,
    hasBefore: true, hasAfter: false, events: [], turns: [finishedTurn] }, chunks: [], runs: [], connection: { generation: 1 } }, () => undefined, false);
  assert.deepEqual(snapshotMessages, large, 'overlapping latest refresh retains all earlier pages');
  assert.deepEqual(snapshotTurns, [oldTurn, finishedTurn], 'earlier facts survive; incoming turn facts win');
  assert.equal(snapshotWindow.beforeCursor, 1);
  assert.equal(snapshotWindow.afterCursor, 300);
  assert.equal(snapshotWindow.hasBefore, false, 'known beginning survives latest refresh');
  assert.equal(snapshotWindow.transferBytes, snapshotSelection.graphBytes);

  const enqueueBlock = source.slice(
    source.indexOf('function enqueueFactory('), source.indexOf('function resetReady(')
  );
  const enqueueFactory = Function(
    'recordLiveMessages', 'takeStreamItem',
    `${stripTypeScriptTypes(enqueueBlock, { mode: 'strip' })}\nreturn enqueueFactory;`
  )(() => undefined, (items: unknown[]) => items.shift());
  const controller = new AbortController();
  const queued = enqueueFactory(controller.signal, { chat: { messages: [] } });
  queued.push({ type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'first', delta: 'one' });
  queued.push({ type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'second', delta: 'two' });
  const iterator = queued.stream()[Symbol.asyncIterator]();
  assert.deepEqual((await iterator.next()).value, { type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'first', delta: 'one' });
  assert.deepEqual((await iterator.next()).value, { type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'second', delta: 'two' });
  controller.abort();
  await iterator.return?.();
});

test('history paging publishes saved rows before import and handles failed or stale reads', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const body = source.slice(source.indexOf('export async function pageSelectedConversation('),
    source.indexOf('export function selectedConversationHistoryOwnedId(')).replace('export async function', 'async function');
  const saved = { id: 'saved', metadata: { firstSequence: 1 } };
  const selection = { historyOwnedId: 'history', beforeCursor: 2, afterCursor: 2,
    controller: new AbortController(), chat: { messages: [saved] },
    push: (chunk: any) => setTimeout(() => {
      assert.equal(historyState.selectedLoadingOlder || historyState.selectedLoadingNewer, true,
        'actual store stays locked until the client consumes the page');
      assert.equal(store.setSelectedConversationPageLoading('history', 'older', true), false);
      assert.equal(store.setSelectedConversationPageLoading('history', 'newer', true), false);
      if (chunk.name === 'assembly:snapshot-ready') selection.resolveReady();
    }, 0),
    ready: Promise.resolve(), resolveReady: () => undefined, resubscribe: async () => {
      assert.equal(selection.chat.messages.at(-1)?.id, 'latest', 'last page is consumed before live resubscription');
      selection.resolveReady();
    } };
  let page = { items: [{ itemId: 'saved' }], events: [], turns: [], watermark: 1, transferBytes: 10, oversized: false, hasBefore: false,
    hasAfter: true, hasEarlierTranscript: true, beforeCursor: 1, afterCursor: 1 };
  let current = true;
  let loading = false;
  let pageError = '';
  let imports = 0;
  let published = 0;
  let readFailure = false;
  let importFailure = false;
  let staleImport = false;
  let publishedWindow: any;
  let measured = 0;
  store.ensureConversationSession('history', 'codex');
  const historyState = store.getConversationSession('history');
  historyState.selectedHasBefore = true;
  historyState.selectedHasAfter = false;
  const read = async () => {
    if (readFailure) throw { message: 'Saved page could not be read' };
    return page;
  };
  const paging = Function('active', 'childActive', 'PAGE_BYTES', 'setSelectedConversationPageLoading',
    'readOlderSelectedConversationItems', 'readNewerSelectedConversationItems', 'isCurrent',
    'extendAgentConversationImportFromTauri', 'messagesFromPage', 'mergeMessages', 'pushMessagesSnapshot',
    'historyPosition', 'hasOlderHistory', 'applySelectedConversationPageState', 'messageBytes',
    'getConversationSession', 'restorePageAttachments', 'pageAttachments', 'setSelectedConversationPageError', 'resetReady', 'EventType',
    `${stripTypeScriptTypes(body, { mode: 'strip' })}\nreturn pageSelectedConversation;`
  )(selection, null, 512 * 1024, (id: string, direction: 'older' | 'newer', value: boolean) => {
    loading = value; if (value) pageError = '';
    return store.setSelectedConversationPageLoading(id, direction, value);
  }, read, read, () => current, async () => {
    imports += 1;
    if (importFailure) throw { message: 'Remote machine is not connected' };
    if (staleImport) { current = false; return { added: 0, reachedStart: true }; }
    page = { ...page, items: [{ itemId: 'saved' }] };
    return { added: 1, reachedStart: true };
  }, () => page.items.map((item) => ({ id: item.itemId, metadata: { firstSequence: 1 } })),
  (_existing: unknown, incoming: unknown) => incoming,
  (target: any, messages: any) => { published += 1; target.graphBytes = 10 * messages.length; queueMicrotask(() => { selection.chat.messages = messages; }); },
  () => 1, (value: any) => value.hasBefore || value.hasEarlierTranscript,
  (id: string, value: any, direction: 'older' | 'newer', window: any) => {
    publishedWindow = window; store.applySelectedConversationPageState(id, value, direction, window);
  }, () => { measured += 1; return 10; },
  () => historyState, async () => undefined, () => new Map(),
  (_id: string, message: string) => { pageError = message; },
  (value: typeof selection) => { value.ready = new Promise<void>((resolve) => { value.resolveReady = resolve; }); }, EventType);

  await paging('older');
  assert.equal(imports, 0, 'saved local messages do not wait for provider import');
  assert.equal(published, 1);
  assert.equal(publishedWindow.hasAfter, false, 'older page preserves the known live tail');
  assert.equal(publishedWindow.transferBytes, 10);
  assert.equal(measured, 0, 'the window size comes from the snapshot that already measured every message');
  assert.equal(loading, false);
  page = { ...page, items: [] };
  await paging('older');
  assert.equal(imports, 1, 'empty earlier boundary still imports');
  assert.equal(published, 2);
  historyState.selectedHasBefore = true;
  readFailure = true;
  await paging('older');
  assert.match(pageError, /Could not load older messages: Saved page could not be read/);
  assert.equal(loading, false);
  assert.equal(published, 2, 'read failure preserves the existing history');
  assert.deepEqual(selection.chat.messages, [saved]);
  readFailure = false;
  importFailure = true;
  page = { ...page, items: [] };
  await paging('older');
  assert.match(pageError, /Remote machine is not connected/);
  assert.equal(loading, false);
  assert.equal(published, 2, 'failed import does not replace visible history');
  importFailure = false;
  staleImport = true;
  await paging('older');
  assert.equal(published, 2, 'an old selection cannot publish an import completion');
  assert.equal(pageError, '');
  current = true; staleImport = false;
  store.setSelectedConversationPageLoading('history', 'older', false);
  historyState.selectedHasAfter = true;
  page = { ...page, items: [{ itemId: 'latest' }], hasAfter: false, hasEarlierTranscript: false };
  await paging('newer');
  assert.equal(publishedWindow.hasBefore, true, 'newer page preserves the earlier boundary');
  assert.equal(publishedWindow.hasAfter, false);
  assert.equal(loading, false);
});

test('saved anchor admission keeps current authority and ignores a stale Jump failure', async () => {
  const source = readFileSync(
    new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url),
    'utf8'
  );
  const admissionBlock = source.slice(
    source.indexOf('function admitInitialSnapshot('),
    source.indexOf('function emitEvent(')
  );
  const jumpBlock = source.slice(
    source.indexOf('export async function jumpSelectedConversationToLatest('),
    source.indexOf('export async function pageSelectedConversation(')
  ).replace('export async function', 'async function');
  const makeHarness = (
    selection: Record<string, any>,
    readPage: () => Promise<any>,
    published: any[],
    controls: any[],
    errors: unknown[]
  ) => {
    const dependencies = {
      initialActive: selection,
      isCurrent: (candidate: unknown) => candidate === selection && !selection.controller.signal.aborted,
      getConversationSession: () => ({
        viewByHistoryId: {
          history: { followLatest: false, anchor: { itemId: 'anchor', firstSequence: 50, offsetPx: 12 } }
        }
      }),
      snapshotChunks: (_selection: unknown, snapshot: unknown, _push: unknown, applyControls: boolean) =>
        published.push({ snapshot, applyControls }),
      readNewerSelectedConversationItems: readPage,
      PAGE_BYTES: 512 * 1024,
      applySelectedConversationSnapshotState: (_workspace: string, _history: string, snapshot: unknown) => controls.push(snapshot),
      setConversationSendError: (_workspace: string, message: string) => errors.push(message),
      refreshSelectedConversationChat: async () => { selection.refreshes += 1; }
    };
    return Function(...Object.keys(dependencies), `
      let active = null;
      let childActive = initialActive;
      ${stripTypeScriptTypes(admissionBlock, { mode: 'strip' })}
      ${stripTypeScriptTypes(jumpBlock, { mode: 'strip' })}
      return { admitInitialSnapshot, jumpSelectedConversationToLatest };
    `)(...Object.values(dependencies)) as {
      admitInitialSnapshot(selection: Record<string, any>, snapshot: any, push: (item: unknown) => void): void;
      jumpSelectedConversationToLatest(workspaceOwnedId: string): Promise<void>;
    };
  };
  const makeSelection = () => ({
    token: Symbol('history'), workspaceOwnedId: 'workspace', historyOwnedId: 'history',
    controller: new AbortController(), contentAdmitted: false, anchorRevision: 0,
    anchorAdmission: null as Promise<void> | null, anchorSnapshot: null,
    rejectReady: (error: unknown) => { throw error; }, refreshes: 0
  });
  const snapshot = (generation: number) => ({
    connection: { generation }, pendingEvents: [],
    page: { items: [], events: [], turns: [], hasBefore: false, hasAfter: false, watermark: generation }
  });

  let resolveAnchor!: (page: any) => void;
  const selection = makeSelection();
  const published: any[] = [];
  const controls: any[] = [];
  const errors: unknown[] = [];
  const api = makeHarness(selection, () => new Promise((resolve) => { resolveAnchor = resolve; }), published, controls, errors);
  api.admitInitialSnapshot(selection, snapshot(1), () => undefined);
  api.admitInitialSnapshot(selection, snapshot(2), () => undefined);
  assert.deepEqual(controls.map((value) => value.connection.generation), [1, 2], 'controls use snapshots immediately');
  const admission = selection.anchorAdmission!;
  resolveAnchor({ items: [], events: [], turns: [], hasBefore: true, hasAfter: true, watermark: 55 });
  await admission;
  assert.equal(published[0].snapshot.connection.generation, 2, 'the newest snapshot remains authoritative');
  assert.equal(published[0].snapshot.page.watermark, 55, 'the saved window supplies only the content page');
  assert.equal(published[0].applyControls, false, 'installing the saved page cannot roll controls back');

  let rejectAnchor!: (error: Error) => void;
  const staleSelection = makeSelection();
  const staleErrors: unknown[] = [];
  staleSelection.rejectReady = (error: unknown) => staleErrors.push(error);
  const staleApi = makeHarness(
    staleSelection,
    () => new Promise((_resolve, reject) => { rejectAnchor = reject; }),
    [], [], staleErrors
  );
  staleApi.admitInitialSnapshot(staleSelection, snapshot(1), (item) => staleErrors.push(item));
  const staleAdmission = staleSelection.anchorAdmission!;
  await staleApi.jumpSelectedConversationToLatest('workspace', 'history');
  rejectAnchor(new Error('obsolete anchor read'));
  await staleAdmission.catch(() => undefined);
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.deepEqual(staleErrors, [], 'Jump invalidates a late anchor rejection');
  assert.equal(staleSelection.refreshes, 1);

  const latestSelection = makeSelection();
  const latestPublished: unknown[] = [];
  latestSelection.resubscribe = async () => { latestSelection.refreshes += 1; };
  const latestApi = makeHarness(
    latestSelection,
    async () => ({ items: [], events: [], turns: [], hasBefore: true, hasAfter: false, watermark: 60 }),
    latestPublished, [], []
  );
  latestApi.admitInitialSnapshot(latestSelection, snapshot(1), () => undefined);
  await latestSelection.anchorAdmission;
  assert.equal(latestSelection.refreshes, 1, 'an anchor page at the tail uses the authoritative resubscribe handoff');
  assert.deepEqual(latestPublished, []);
});

test('child history admission rejects stale results and stops the exact native watch', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(
    source.indexOf('interface ChildHistoryRead'),
    source.indexOf('export type SavedAttachment')
  ).replaceAll('export ', '');
  const pending = new Map<number, { resolve(value: any): void; reject(error: Error): void }>();
  const reads: any[] = [];
  const stops: Array<[string, number]> = [];
  const statuses: Array<[string, string, boolean]> = [];
  const failures: Array<[string, string, string]> = [];
  const state = { selectedChildId: 'child-a' };
  let nextRequestId = 0;
  const dependencies = {
    createAgentConversationRequestId: () => ++nextRequestId,
    CHILD_HISTORY_PAGE_BYTES: 512 * 1024,
    clearConversationHistoryDownload: () => undefined,
    attachHistoryDownload: async () => undefined,
    readAgentConversationChildHistoryFromTauri: (request: any) => {
      reads.push(request);
      return new Promise((resolve, reject) => pending.set(request.requestId, { resolve, reject }));
    },
    stopAgentConversationChildHistoryFromTauri: async (ownedId: string, requestId: number) => {
      stops.push([ownedId, requestId]); return true;
    },
    getConversationSession: () => state,
    applyChildConversationHistoryStatus: (ownedId: string, childId: string, truncated: boolean) =>
      statuses.push([ownedId, childId, truncated]),
    failChildConversationTranscript: (ownedId: string, childId: string, message: string) =>
      failures.push([ownedId, childId, message])
  };
  const api = Function(...Object.keys(dependencies), `
    ${stripTypeScriptTypes(block, { mode: 'strip' })}
    return { readChildConversationHistory, stopChildConversationHistory };
  `)(...Object.values(dependencies)) as {
    readChildConversationHistory(input: any): Promise<any>;
    stopChildConversationHistory(ownedId: string): void;
  };
  const first = api.readChildConversationHistory({
    ownedId: 'parent', childId: 'child-a', childSessionId: 'durable-a'
  });
  await new Promise<void>((resolve) => setImmediate(resolve));
  api.stopChildConversationHistory('parent');
  state.selectedChildId = 'child-b';
  const second = api.readChildConversationHistory({
    ownedId: 'parent', childId: 'child-b', childSessionId: 'durable-b'
  });
  state.selectedChildId = 'child-c';
  const third = api.readChildConversationHistory({
    ownedId: 'parent', childId: 'child-c', childSessionId: 'durable-c'
  });
  assert.deepEqual(reads.map((read) => read.requestId), [1],
    'B and C serialize behind the deferred A admission');
  const page = { hasEarlierTranscript: true };
  pending.get(1)!.resolve({ historyOwnedId: 'obsolete-history', page });
  assert.equal(await first, null, 'a stale completion cannot steal the newer selection');
  assert.equal(await second, null, 'B is cancelled before it invokes native selection');
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.deepEqual(reads.map((read) => read.requestId), [1, 3], 'only A then current C invoke native selection');
  assert.deepEqual(stops.map(([, requestId]) => requestId), [1, 1, 2, 1],
    'the late A result receives a second exact stop without stopping C');
  pending.get(3)!.resolve({ historyOwnedId: 'opaque-history', page });
  assert.equal((await third).historyOwnedId, 'opaque-history');
  assert.deepEqual(statuses, [['parent', 'child-c', true]]);

  const failed = api.readChildConversationHistory({
    ownedId: 'parent', childId: 'child-c', childSessionId: 'durable-c'
  });
  await new Promise<void>((resolve) => setImmediate(resolve));
  pending.get(4)!.reject(new Error('refresh failed'));
  await failed.catch(() => undefined);
  assert.deepEqual(failures, [['parent', 'child-c', 'refresh failed']]);
  assert.deepEqual(stops.at(-1), ['parent', 4], 'a failed admission releases its native watch');
});

test('remote reconnect reacquires only the still-selected child watch', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(
    source.indexOf('async function reacquireSelectedChildHistory('),
    source.indexOf('function handleConversationStreamEnvelope(')
  );
  const read = {
    workspaceOwnedId: 'parent', ownedId: 'opaque-child', abortController: new AbortController(),
  };
  const state = {
    selectedChildId: 'child-a',
    children: [{
      childId: 'child-a', transcriptId: 'durable-a', transcriptAvailable: true
    }]
  };
  const admissions: any[] = [];
  const pending: Array<(value: any) => void> = [];
  let reloads = 0;
  const api = Function(
    'initialRead', 'rail', 'getConversationSession', 'readChildConversationHistory',
    'requestSelectedConversationReload',
    `let conversationEventsDisposed = false;
     let conversationEventsGeneration = 7;
     let selectedConversationRead = null;
     let childConversationRead = initialRead;
     const isCurrentConversationRead = (candidate) => candidate === childConversationRead && !candidate.abortController.signal.aborted;
     ${stripTypeScriptTypes(block, { mode: 'strip' })}
     return { reacquireSelectedChildHistory };`
  )(
    read,
    { owned: [{ ownedId: 'parent', remoteProfileId: 'remote-a' }] },
    () => state,
    (input: any) => {
      admissions.push(input);
      return new Promise((resolve) => pending.push(resolve));
    },
    () => {
      reloads += 1;
    }
  ) as { reacquireSelectedChildHistory(profileId: string, generation: number): Promise<void> };

  await api.reacquireSelectedChildHistory('remote-b', 7);
  assert.equal(admissions.length, 0, 'another remote profile cannot replace the selected child watch');

  const stale = api.reacquireSelectedChildHistory('remote-a', 7);
  assert.equal(admissions[0].childSessionId, 'durable-a');
  state.selectedChildId = 'child-b';
  pending[0]({ historyOwnedId: 'opaque-child' });
  await stale;
  assert.equal(reloads, 0, 'a changed child selection rejects the late reacquisition');

  state.selectedChildId = 'child-a';
  const current = api.reacquireSelectedChildHistory('remote-a', 7);
  pending[1]({ historyOwnedId: 'opaque-child' });
  await current;
  assert.equal(reloads, 1, 'the reacquired watch reloads the existing selected child graph');
});

test('parent and child readers deliver concurrently without sharing cursors', () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('function selectedEventUsesControlCursor('), source.indexOf('async function refreshSelectedConversation('));
  const delivered: string[] = [];
  const makeRead = (ownedId: string) => ({
    ownedId, generation: 1, pageWatermark: 0, pendingSequence: 0, reading: false,
    abortController: new AbortController(),
    onEvent: (event: AgentConversationEvent) => delivered.push(event.ownedId)
  });
  const parent = makeRead('parallel-parent');
  const child = makeRead('parallel-child');
  const dependencies = {
    selectedConversationRead: parent, childConversationRead: child,
    isCurrentConversationRead: (read: typeof parent) => !read.abortController.signal.aborted && (read === parent || read === child)
  };
  const admit = Function(...Object.keys(dependencies), `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn admitSelectedConversationEvent;`)(...Object.values(dependencies));
  const event = (ownedId: string, sequence: number) => ({
    ownedId, provider: 'codex', generation: 1, sequence, timestampMs: sequence,
    payload: { kind: 'assistantDelta', itemId: ownedId, delta: 'text' }
  });
  assert.equal(admit(event(parent.ownedId, 1)), true);
  assert.equal(admit(event(child.ownedId, 1)), true);
  child.abortController.abort();
  assert.equal(admit(event(child.ownedId, 2)), false);
  assert.equal(admit(event(parent.ownedId, 2)), true);
  assert.deepEqual(delivered, [parent.ownedId, child.ownedId, parent.ownedId]);
  assert.equal(parent.pageWatermark, 2);
  assert.equal(child.pageWatermark, 1);
});

await test('start-up registers the event stream before it opens the remembered session', async () => {
  // A remote open shows the Mac's copy at once and its background top-up is
  // published moments later; a stream registered after the open would miss it.
  const source = readFileSync(new URL('../src/lib/shell/controllers/shellStartup.ts', import.meta.url), 'utf8');
  const startShell = source.slice(source.indexOf('export async function startShell('), source.indexOf('export function stopShell('));
  const calls: string[] = [];
  const dependencies = {
    document: { documentElement: { classList: { add: () => undefined } } },
    applyStoredTheme: () => undefined, applyStoredFonts: () => undefined,
    hydrateShellSettings: async () => undefined, shellActive: () => true,
    listAgentConversationSessionsFromTauri: async () => [],
    readRemoteSessions: async () => [{ ownedId: 'remembered' }],
    readStoredActiveOwnedId: async () => 'remembered', hydrateProjects: async () => undefined,
    ownedSessionFromBackend: (session: unknown) => session, hydrateOwned: () => undefined,
    shellPanels: { allowSessionLoads: () => undefined }, rail: { error: null },
    startConversationEventsForOwner: async () => {
      await new Promise((resolve) => setTimeout(resolve, 5));
      calls.push('event stream registered');
    }
  };
  const shell = Function(...Object.keys(dependencies), stripTypeScriptTypes(`
    let shellAbort = null; let shellGeneration = 0;
    ${startShell.replace('export async function', 'async function')}
  `, { mode: 'strip' }) + '\nreturn { startShell };')(...Object.values(dependencies));
  await shell.startShell({ onSelectInitial: async (ownedId: string) => { calls.push('open ' + ownedId); } });
  assert.deepEqual(calls, ['event stream registered', 'open remembered']);
});

await test('ensure refresh requires the confirmed generation before admitting cached history', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const reload = source.slice(source.indexOf('async function reloadSelectedConversation('), source.indexOf('export async function subscribeSelectedConversation('));
  const ensure = source.slice(source.indexOf('async function ensureStructuredConversationOnce('), source.indexOf('/** Changes a quiescent session'));
  const ownedId = 'ensured-generation';
  for (const priorMinimum of [undefined, 3]) {
    const state = { generation: 0 };
    const reads: Array<number | undefined> = [];
    const read = {
      ownedId, workspaceOwnedId: ownedId, maxBytes: 512 * 1024,
      minimumGeneration: priorMinimum, reading: false, reloadRequested: false,
      events: [], chunks: [], bytes: 0, overflow: false, abortController: new AbortController(),
      onSnapshot: (snapshot: { connection: { generation: number } }) => { state.generation = snapshot.connection.generation; }
    };
    const dependencies = {
      selectedConversationRead: read, childConversationRead: null, isCurrentConversationRead: (candidate: unknown) => candidate === read && !read.abortController.signal.aborted, ensuring: new Map(), rail: { activeOwnedId: ownedId },
      invoke: async () => ({ ownedId, provider: 'codex', generation: 1 }),
      setConversationConnection: (connection: { generation: number }) => { state.generation = connection.generation; },
      readAgentConversationSelectionFromTauri: async (_id: string, _bytes: number, minimum?: number) => {
        reads.push(minimum);
        // The remote boundary serves cached generation 0 unless the caller requires newer metadata.
        return { connection: { generation: minimum ?? 0 }, page: { watermark: 6 }, pendingSequence: 6 };
      }
    };
    const ensured = Function(...Object.keys(dependencies),
      stripTypeScriptTypes(`${reload}\n${ensure}`, { mode: 'strip' }) + '\nreturn ensureStructuredConversationOnce;'
    )(...Object.values(dependencies)) as (
      id: string, request: object, token: object, signal: AbortSignal
    ) => Promise<{ generation: number }>;
    await ensured(ownedId, {}, {}, new AbortController().signal);
    const expectedMinimum = priorMinimum ?? 1;
    assert.deepEqual(reads, [expectedMinimum], 'the selected read requires the ensured or already newer generation');
    assert.equal(state.generation, expectedMinimum, 'cached generation 0 cannot replace the confirmed generation');
  }
});

await test('sendStructuredMessage resolves the terminal from the owned session, not a passed argument', async () => {
  const ownedId = 'owned-terminal-route';
  const terminalId = 'terminal-from-owned-session';
  const serviceSource = readFileSync(
    new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url),
    'utf8'
  ).match(
    /export async function sendStructuredMessage\([\s\S]*?\n\}\n\n\/\*\* Stops the active turn/
  );
  assert.ok(serviceSource);
  const serviceJavaScript = stripTypeScriptTypes(
    serviceSource[0]
      .replace(/\n\n\/\*\* Stops the active turn$/, '')
      .replace('export async function', 'async function'),
    { mode: 'strip' }
  );
  const state = {
    attachments: [],
    generation: 0,
    writerLease: { ownedId, generation: 0, owner: 'terminal' }
  };
  const terminalWrites: Array<{ terminalId: string; text: string }> = [];
  const sendStructuredMessage = Function(
    'getConversationSession',
    'setConversationSending',
    'rail',
    'get',
    'sessionPresenceHistory',
    'shouldReviveBeforeSend',
    'writeTerminalSessionFromTauri',
    'cleanupConversationAttachmentPreview',
    'setConversationAttachments',
    'recordSentConversationAttachments',
    `const preparingSends = new Map();\n${serviceJavaScript}\nreturn sendStructuredMessage;`
  )(
    () => state,
    () => undefined,
    {
      activeOwnedId: 'another-session',
      owned: [{
        ownedId,
        ptySessionId: terminalId,
        origin: 'external',
        state: 'live',
        executionOwner: 'terminal'
      }]
    },
    get,
    sessionPresenceHistory,
    () => false,
    async (usedTerminalId: string, text: string) => {
      terminalWrites.push({ terminalId: usedTerminalId, text });
      return true;
    },
    () => undefined,
    () => undefined,
    () => undefined
  ) as (messageOwnedId: string, text: string) => Promise<void>;

  await sendStructuredMessage(ownedId, 'Route this message');
  assert.deepEqual(terminalWrites.map((write) => write.terminalId), [terminalId, terminalId]);
});

await test('two quick sends with screenshots each keep their own thumbnails', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.match(/export async function sendStructuredMessage\([\s\S]*?\n\}\n\n\/\*\* Stops the active turn/);
  assert.ok(block);
  const code = stripTypeScriptTypes(block[0].replace(/\n\n\/\*\* Stops the active turn$/, '').replace('export async function', 'async function'), { mode: 'strip' });
  const ownedId = 'owned-two-sends';
  store.applySelectedConversationSnapshotState(ownedId, ownedId, {
    connection: { ownedId, provider: 'claude', generation: 1, state: 'connected' },
    suspended: false,
    page: {
      items: [], events: [], turns: [], hasBefore: false, hasEarlierTranscript: false,
      hasAfter: false, watermark: 0, transferBytes: 0, oversized: false
    },
    pendingEvents: [], pendingSequence: 0
  });
  const shot = (id: string) => ({ id, name: `${id}.png`, mimeType: 'image/png', path: `/managed/${id}.png`, previewUrl: `blob:${id}` });
  const dependencies = {
    getConversationSession: store.getConversationSession,
    setConversationSending: () => undefined,
    rail: { activeOwnedId: ownedId, owned: [{ ownedId, agent: 'claude', state: 'live', origin: 'app', executionEnvironment: 'remote' }] },
    get, sessionPresenceHistory,
    shouldReviveBeforeSend: () => false,
    sendSupportsImages: () => true,
    buildConversationPrompt: (text: string) => ({ text, content: [] }),
    sendTargetGeneration: () => 1,
    updateOwnedSession: () => undefined,
    recordSentConversationAttachments: store.recordSentConversationAttachments,
    attachmentDisplayMetadata: (attachment: object) => ({ ...attachment }),
    invoke: () => new Promise(() => {}),
    setConversationAttachments: store.setConversationAttachments
  };
  const send = Function(...Object.keys(dependencies), `const preparingSends = new Map();\n${code}\nreturn sendStructuredMessage;`)(...Object.values(dependencies)) as (id: string, text: string) => Promise<unknown>;
  store.setConversationAttachments(ownedId, [shot('big')]);
  void send(ownedId, 'First');
  store.setConversationAttachments(ownedId, [shot('small')]);
  void send(ownedId, 'Second');
  // The backend names each message's screenshots on its own user event.
  for (const [sequence, itemId, attachmentId] of [[1, 'user-first', 'big'], [2, 'user-second', 'small']] as const) {
    store.applySelectedConversationEventState(ownedId, {
      ownedId, provider: 'claude', generation: 1, sequence, timestampMs: sequence,
      payload: { kind: 'userMessage', itemId, text: itemId, completed: true, attachmentIds: [attachmentId] }
    });
  }
  const sent = store.getConversationSession(ownedId).sentAttachments;
  assert.deepEqual(sent['user-first']?.map((item: { id: string }) => item.id), ['big']);
  assert.deepEqual(sent['user-second']?.map((item: { id: string }) => item.id), ['small']);
});

await test('Send empties the composer before the request, and a failed send puts the screenshots back', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.match(/export async function sendStructuredMessage\([\s\S]*?\n\}\n\n\/\*\* Stops the active turn/);
  assert.ok(block);
  const code = stripTypeScriptTypes(block[0].replace(/\n\n\/\*\* Stops the active turn$/, '').replace('export async function', 'async function'), { mode: 'strip' });
  const ownedId = 'composer-clears-at-send';
  const shot = { id: 'shot', previewUrl: 'blob:shot' };
  const state = { sending: false, generation: 1, attachments: [shot] as Array<{ id: string; previewUrl: string }>, capabilities: null, connectionState: 'connected', agentConfig: { availableApprovalPolicies: [] } };
  const held: Array<Array<{ id: string }>> = [];
  let settle!: { resolve(value: unknown): void; reject(error: unknown): void };
  const dependencies = {
    getConversationSession: () => state,
    setConversationSending: (_id: string, sending: boolean) => { state.sending = sending; },
    rail: { activeOwnedId: ownedId, owned: [{ ownedId, agent: 'claude', state: 'live', origin: 'app', executionEnvironment: 'remote' }] },
    get, sessionPresenceHistory,
    shouldReviveBeforeSend: () => false,
    sendSupportsImages: () => true,
    buildConversationPrompt: (text: string) => ({ text, content: [] }),
    sendTargetGeneration: () => 1,
    updateOwnedSession: () => undefined,
    recordSentConversationAttachments: (_id: string, list: Array<{ id: string }>) => { held.push(list); },
    attachmentDisplayMetadata: (attachment: { id: string }) => ({ ...attachment }),
    invoke: () => new Promise((resolve, reject) => { settle = { resolve, reject }; }),
    setConversationAttachments: (_id: string, list: Array<{ id: string; previewUrl: string }>) => { state.attachments = list; }
  };
  const send = Function(...Object.keys(dependencies), `const preparingSends = new Map();\n${code}\nreturn sendStructuredMessage;`)(...Object.values(dependencies)) as (id: string, text: string) => Promise<unknown>;

  const failing = send(ownedId, 'Look at this');
  assert.deepEqual(state.attachments, [], 'the composer is empty before the request goes out');
  assert.deepEqual(held.at(-1)?.map((item) => item.id), ['shot'], 'the transcript holds the screenshots for the sent message');
  state.attachments = [{ id: 'pasted-later', previewUrl: 'blob:later' }];
  settle.reject(new Error('offline'));
  await assert.rejects(failing, /offline/);
  assert.deepEqual(state.attachments.map((item) => item.id), ['shot', 'pasted-later'],
    'a failed send puts its screenshots back without dropping ones added since');

  const sent = send(ownedId, 'Look again');
  state.attachments = [{ id: 'pasted-during-send', previewUrl: 'blob:during' }];
  settle.resolve({ ownedId, generation: 1, turnId: 'turn', userItemId: 'user', admittedSequence: 1 });
  await sent;
  assert.deepEqual(state.attachments.map((item) => item.id), ['pasted-during-send'],
    'a delivered send does not clear screenshots pasted while it was in flight');
});

await test('an admitted send returns the native receipt', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.match(/export async function sendStructuredMessage\([\s\S]*?\n\}\n\n\/\*\* Stops the active turn/);
  assert.ok(block);
  const code = stripTypeScriptTypes(block[0].replace(/\n\n\/\*\* Stops the active turn$/, '').replace('export async function', 'async function'), { mode: 'strip' });
  for (const executionEnvironment of ['local', 'remote']) {
    const ownedId = `accepted-${executionEnvironment}`;
    const receipt = { ownedId, generation: 1, turnId: 'turn-accepted', userItemId: 'user-accepted', admittedSequence: 4 };
    const state = { sending: false, generation: 1, attachments: [], capabilities: null, connectionState: 'connected', agentConfig: { availableApprovalPolicies: [] } };
    let sends = 0;
    const dependencies = {
      getConversationSession: () => state,
      setConversationSending: (_id: string, sending: boolean) => { state.sending = sending; },
      rail: { activeOwnedId: ownedId, owned: [{ ownedId, agent: 'codex', state: 'live', origin: 'app', executionEnvironment }] },
      get, sessionPresenceHistory,
      shouldReviveBeforeSend: () => false,
      sendSupportsImages: () => false,
      buildConversationPrompt: (text: string) => ({ text, content: [] }),
      hasBackendCapability: async () => false,
      ACP_LIVE_CONVERSATION_EVENTS_CAPABILITY: 'test',
      sendTargetGeneration: () => 1,
      updateOwnedSession: () => undefined,
      recordSentConversationAttachments: () => undefined,
      attachmentDisplayMetadata: () => undefined,
      invoke: async () => { sends++; return receipt; },
      setConversationAttachments: () => undefined
    };
    const send = Function(...Object.keys(dependencies), `const preparingSends = new Map();\n${code}\nreturn sendStructuredMessage;`)(...Object.values(dependencies)) as (id: string, text: string) => Promise<unknown>;
    assert.deepEqual(await send(ownedId, 'Continue'), receipt);
    assert.equal(sends, 1);
    assert.equal(state.sending, true, 'receipt admission keeps the turn active');
  }
});

test('file notifications and runtime state do not depend on the selected transcript', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const handler = source.slice(source.indexOf('function handleConversationStreamEnvelope('), source.indexOf('async function handleConversationStreamResync('));
  for (const selection of ['root', 'child', 'another-session']) {
    const ownedId = `file-notification-${selection}`;
    const runtimeStates: string[] = [];
    const notifications: Array<{ ownedId: string; path: string }> = [];
    const current = selection === 'another-session' ? null : { generation: 1, selectedChildId: selection === 'child' ? 'child' : null };
    const dependencies = {
      conversationEventsDisposed: false, conversationEventsGeneration: 1,
      admitSelectedConversationEvent: () => selection === 'root',
      childConversationRead: null,
      rail: { owned: [{ ownedId }] }, adoptSession: async () => false,
      railActivityEvents: new Map(), selectedConversationRead: selection === 'child' ? { workspaceOwnedId: ownedId, ownedId: 'child-history' } : null,
      displayEventFrom: store.displayEventFrom, shouldClearConversationSending,
      getConversationSession: () => current, selectedEventUsesControlCursor: () => false,
      get: () => ({}), sessionPresenceHistory: {},
      agentItemFromEvent, displayItemFromAgentItem,
      publishWorkspaceFileChange: (change: { ownedId: string; path: string }) => notifications.push(change),
      sessionPresenceEventFromConversation: () => null,
      recordAgentConversationPresenceEvent: () => undefined,
      updateOwnedSession: (_id: string, patch: { runtimeState?: string }) => {
        if (patch.runtimeState) runtimeStates.push(patch.runtimeState);
      },
      synchronizeSessionPresenceWork: () => undefined, setConversationSending: () => undefined
    };
    const deliver = Function(...Object.keys(dependencies), `${stripTypeScriptTypes(handler, { mode: 'strip' })}\nreturn handleConversationStreamEnvelope;`)(...Object.values(dependencies));
    const event = (sequence: number, generation = 1) => ({
      ownedId, provider: 'codex', generation, sequence, timestampMs: sequence,
      payload: { kind: 'tool', itemId: 'edit-1', name: 'Edit', state: sequence === 1 ? 'started' : 'completed',
        path: '/workspace/new.txt', diff: '@@ -0,0 +1 @@\n+created\n' }
    });
    await deliver(1, { chunk: event(1) });
    assert.deepEqual(notifications, [], 'opening an edit is not a completed file change');
    await deliver(1, { chunk: event(2) });
    await deliver(1, { chunk: event(2) });
    await deliver(1, { chunk: event(3, 0) });
    assert.deepEqual(notifications, [{ ownedId, path: '/workspace/new.txt' }], `${selection}: only a fresh completion publishes once`);
    await deliver(1, { chunk: { ...event(4), runtimeState: 'working',
      payload: { kind: 'turn', turnId: 'foreground', state: 'completed' } } });
    assert.equal(runtimeStates.at(-1), 'working', 'background work outlives foreground completion');
    await deliver(1, { chunk: { ...event(5), runtimeState: 'ready',
      payload: { kind: 'connection', state: 'connected' } } });
    assert.equal(runtimeStates.at(-1), 'ready', 'completion or Stop clears background Working');
  }
});

store.removeConversationSession('owned-a');
assert.equal(store.getConversationSession('owned-a'), null);
assert.ok(store.getConversationSession('owned-b'));

await test('Stop during revival prevents dispatch and releases the prepared runtime', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('const preparingSends ='), source.indexOf('/** Answers one summary-only approval'));
  const code = stripTypeScriptTypes(block.replaceAll('export async function', 'async function'), { mode: 'strip' });
  let finishRevival!: () => void;
  const revival = new Promise<void>((resolve) => { finishRevival = resolve; });
  const state = { sending: false, generation: 1, attachments: [], agentConfig: {}, connectionState: 'disconnected' };
  const calls: string[] = [];
  const dependencies = {
    getConversationSession: () => state,
    setConversationSending: (_id: string, sending: boolean) => { state.sending = sending; },
    get, normalizeProvider,
    sessionPresenceHistory,
    rail: { owned: [{ ownedId: 'cancel-test', agent: 'antigravity', cwd: '/tmp', nativeSessionId: 'native-original' }] },
    shouldReviveBeforeSend: () => true,
    decideConversationActivation: () => ({ kind: 'structured', nativeSessionMode: 'resume' }),
    ensureStructuredConversation: async () => { await revival; state.generation = 2; return { generation: 2 }; },
    generationForSend: (_before: number, after: number) => after, setConversationConnection: () => undefined,
    sendSupportsImages: () => false,
    buildConversationPrompt: (text: string) => ({ text, content: [] }),
    hasBackendCapability: async () => true,
    ACP_LIVE_CONVERSATION_EVENTS_CAPABILITY: 'test',
    sendTargetGeneration: () => state.generation,
    updateOwnedSession: () => undefined,
    recordSentConversationAttachments: () => undefined,
    attachmentDisplayMetadata: () => undefined,
    invoke: async (command: string) => { calls.push(command); },
    setConversationAttachments: () => undefined
  };
  const api = Function(...Object.keys(dependencies), `${code}\nreturn { sendStructuredMessage, stopStructuredTurn, preparingSends };`)(...Object.values(dependencies)) as {
    sendStructuredMessage: (id: string, text: string) => Promise<void>;
    stopStructuredTurn: (id: string) => Promise<void>;
    preparingSends: Map<string, unknown>;
  };
  const sending = api.sendStructuredMessage('cancel-test', 'Must not reach the provider');
  const cancelled = assert.rejects(sending, /cancelled before sending/);
  await api.stopStructuredTurn('cancel-test');
  finishRevival();
  await cancelled;
  assert.deepEqual(calls, ['stop_agent_conversation_turn']);
  assert.equal(state.sending, false);
  assert.equal(api.preparingSends.size, 0, 'no retained send bookkeeping');
});

await test('Stop during the image send wait skips hydration and writes no session state', async () => {
  const ownedId = 'glide-stop';
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('const preparingSends ='), source.indexOf('/** Answers one summary-only approval'));
  const code = stripTypeScriptTypes(block.replaceAll('export async function', 'async function'), { mode: 'strip' });
  const state = {
    sending: false, generation: 1, activeTurnId: undefined,
    attachments: [{ id: 'image-a', name: 'a.png', mimeType: 'image/png', path: '/tmp/a.png' }],
    capabilities: null, connectionState: 'connected', agentConfig: { availableApprovalPolicies: [] }
  };
  const calls: string[] = [];
  const writes: string[] = [];
  let hydrated = 0;
  const dependencies = {
    getConversationSession: () => state,
    setConversationSending: (_id: string, sending: boolean) => { state.sending = sending; },
    get,
    sessionPresenceHistory,
    rail: { owned: [{ ownedId, agent: 'claude', state: 'live', origin: 'app', executionEnvironment: 'local', nativeSessionId: 'native-glide' }] },
    shouldReviveBeforeSend: () => false,
    sendSupportsImages: () => true,
    hydrateAttachmentBytes: async (attachment: object) => { hydrated += 1; return { ...attachment, bytes: [1] }; },
    buildConversationPrompt: (text: string) => ({ text, content: [] }),
    sendTargetGeneration: () => 1,
    updateOwnedSession: () => { writes.push('owned session'); },
    recordSentConversationAttachments: () => undefined,
    attachmentDisplayMetadata: () => undefined,
    invoke: async (command: string) => { calls.push(command); },
    setConversationAttachments: () => undefined
  };
  const api = Function(...Object.keys(dependencies), `${code}\nreturn { sendStructuredMessage, stopStructuredTurn, preparingSends };`)(...Object.values(dependencies)) as {
    sendStructuredMessage: (id: string, text: string) => Promise<void>;
    stopStructuredTurn: (id: string) => Promise<void>;
    preparingSends: Map<string, unknown>;
  };
  const sending = api.sendStructuredMessage(ownedId, 'Look at this');
  const cancelled = assert.rejects(sending, /^Error: Message cancelled before sending\.$/);
  await api.stopStructuredTurn(ownedId);
  await cancelled;
  assert.equal(hydrated, 0, 'the image bytes are not read after Stop');
  assert.deepEqual(writes, [], 'no session state is written after Stop');
  assert.deepEqual(calls, ['stop_agent_conversation_turn']);
  assert.equal(state.sending, false);
  assert.equal(api.preparingSends.size, 0);
});

await test('an observed active turn routes Stop to the backend', async () => {
  const ownedId = 'observed-steering';
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('const preparingSends ='), source.indexOf('/** Answers one summary-only approval'));
  const code = stripTypeScriptTypes(block.replaceAll('export async function', 'async function'), { mode: 'strip' });
  store.recordAgentConversationPresenceEvent({
    ownedId, provider: 'claude', generation: 1, sequence: 1, timestampMs: 3_000,
    payload: { kind: 'turn', turnId: 'observed-turn', state: 'started' }
  });
  const state = {
    sending: false, generation: 1, activeTurnId: undefined,
    attachments: [], capabilities: null, connectionState: 'connected',
    agentConfig: { availableApprovalPolicies: [] }
  };
  const calls: string[] = [];
  const dependencies = {
    getConversationSession: () => state,
    setConversationSending: (_id: string, sending: boolean) => { state.sending = sending; },
    get,
    sessionPresenceHistory,
    rail: { owned: [{ ownedId, agent: 'claude', state: 'live', origin: 'app', nativeSessionId: 'native-observed' }] },
    shouldReviveBeforeSend: () => false,
    sendSupportsImages: () => false,
    buildConversationPrompt: (text: string) => ({ text, content: [] }),
    sendTargetGeneration: () => 1,
    updateOwnedSession: () => undefined,
    recordSentConversationAttachments: () => undefined,
    attachmentDisplayMetadata: () => undefined,
    invoke: async (command: string) => { calls.push(command); },
    setConversationAttachments: () => undefined
  };
  const api = Function(...Object.keys(dependencies), `${code}\nreturn { sendStructuredMessage, stopStructuredTurn, preparingSends };`)(...Object.values(dependencies)) as {
    sendStructuredMessage: (id: string, text: string) => Promise<void>;
    stopStructuredTurn: (id: string) => Promise<void>;
    preparingSends: Map<string, unknown>;
  };
  await api.sendStructuredMessage(ownedId, 'Correct the answer');
  await api.stopStructuredTurn(ownedId);
  assert.deepEqual(calls, ['send_agent_conversation_message', 'stop_agent_conversation_turn']);
  assert.equal(api.preparingSends.size, 0);
});

await test('an inactive terminal event clears send state before finished rail presence', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('function handleConversationStreamEnvelope('), source.indexOf('async function handleConversationStreamResync('));
  const code = stripTypeScriptTypes(block, { mode: 'strip' }).replace(/^export /gm, '');
  const calls: string[] = [];
  const ownedId = 'inactive-finish';
  const draft = 'Keep this draft';
  const attachments = [{ id: 'unsent-image' }];
  const state = { generation: 1, sending: true, draft, attachments, activeTurnId: null as string | null };
  const dependencies = {
    conversationEventsDisposed: false,
    conversationEventsGeneration: 1,
    selectedConversationRead: null,
    childConversationRead: null,
    railActivityEvents: new Map(),
    sessionPresenceEventFromConversation,
    synchronizeSessionPresenceWork,
    displayEventFrom: store.displayEventFrom,
    agentItemFromEvent, displayItemFromAgentItem, publishWorkspaceFileChange: assert.fail,
    updateOwnedSession: () => undefined,
    rail: { activeOwnedId: 'another-session', owned: [] },
    adoptSession: async () => false,
    shouldClearConversationSending,
    get,
    sessionPresenceHistory,
    admitSelectedConversationEvent: () => false,
    setConversationSending: () => { state.sending = false; calls.push('clear'); },
    recordAgentConversationPresenceEvent: () => { calls.push('presence'); },
    getConversationSession: () => state
  };
  const handle = Function(...Object.keys(dependencies), `${code}\nreturn handleConversationStreamEnvelope;`)(...Object.values(dependencies)) as (
    generation: number, envelope: { chunk: AgentConversationEvent }
  ) => Promise<void>;
  await handle(1, { chunk: {
    ownedId, provider: 'claude', generation: 1, sequence: 3, timestampMs: 3_010,
    payload: { kind: 'turn', turnId: 'observed-turn', state: 'completed' }
  } });
  assert.deepEqual(calls, ['presence', 'clear']);
  assert.equal(state.draft, draft);
  assert.equal(state.attachments, attachments);
  store.recordAgentConversationPresenceEvent({
    ownedId, provider: 'claude', generation: 2, sequence: 4, timestampMs: 3_020,
    payload: { kind: 'turn', turnId: 'newer-turn', state: 'started' }
  });
  state.generation = 2;
  state.sending = true;
  calls.length = 0;
  await handle(1, { chunk: {
    ownedId, provider: 'claude', generation: 1, sequence: 3, timestampMs: 3_010,
    payload: { kind: 'turn', turnId: 'older-turn', state: 'completed' }
  } });
  assert.deepEqual(calls, [], 'an older generation cannot clear the current send');
  assert.equal(state.sending, true);
  assert.equal(get(sessionPresenceHistory)[ownedId].activeTurnId, 'newer-turn');
  await handle(1, { chunk: {
    ownedId, provider: 'claude', generation: 2, sequence: 5, timestampMs: 3_030,
    payload: { kind: 'turn', turnId: 'older-turn', state: 'completed' }
  } });
  assert.deepEqual(calls, [], 'an older turn cannot clear a newer turn in the same generation');
  assert.equal(get(sessionPresenceHistory)[ownedId].activeTurnId, 'newer-turn');
  sessionPresenceHistory.update((records) => {
    const next = { ...records };
    delete next[ownedId];
    return next;
  });
  state.activeTurnId = 'newer-turn';
  await handle(1, { chunk: {
    ownedId, provider: 'claude', generation: 2, sequence: 5, timestampMs: 3_030,
    payload: { kind: 'turn', turnId: 'older-turn', state: 'completed' }
  } });
  assert.deepEqual(calls, [], 'the active projection also rejects an older turn');
  assert.equal(state.sending, true);
});

await test('remote rail activity reconciles without opening background conversations', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('async function refreshRemoteSessionActivity('), source.indexOf('// A session an agent started'))
    + source.slice(source.indexOf('function handleConversationStreamEnvelope('), source.indexOf('async function handleConversationStreamResync('));
  const code = stripTypeScriptTypes(block, { mode: 'strip' }).replace(/^export /gm, '');
  const ownedId = 'remote-background-activity';
  const row = {
    ownedId, state: 'background', runtimeState: 'suspended', activeTurnId: null as string | null,
    pendingPermission: false, pendingInput: false, lastError: null
  };
  const state = { activeTurnId: undefined as string | undefined, suspended: true, sending: false, generation: 1 };
  let opened = false;
  let finishRead: (records: unknown[]) => void = () => {};
  let readStarted = () => {};
  const dependencies = {
    selectedConversationRead: null,
    childConversationRead: null,
    rail: { activeOwnedId: 'another-session', owned: [row], remoteConnections: { workbox: 'connected' }, error: null },
    listRemoteAgentConversationSessionsFromTauri: () => new Promise<unknown[]>((resolve) => { finishRead = resolve; readStarted(); }),
    updateOwnedSession: (_id: string, patch: Partial<typeof row>) => Object.assign(row, patch),
    getConversationSession: () => opened ? state : null,
    preparingSends: new Map(),
    get, sessionPresenceHistory, sessionPresenceEventFromConversation, synchronizeSessionPresenceWork,
    displayEventFrom: store.displayEventFrom,
    agentItemFromEvent, displayItemFromAgentItem, publishWorkspaceFileChange: assert.fail,
    shouldClearConversationSending,
    setConversationSending: (_id: string, sending: boolean) => { state.sending = sending; },
    recordAgentConversationPresenceEvent: store.recordAgentConversationPresenceEvent,
    admitSelectedConversationEvent: () => false
  };
  const api = Function(...Object.keys(dependencies), `
    let conversationEventsDisposed = false;
    let conversationEventsGeneration = 1;
    let remoteActivityController = null;
    const railActivityEvents = new Map();
    ${code}
    return { refresh: () => refreshRemoteSessionActivity(1),
      event: chunk => handleConversationStreamEnvelope(1, {chunk}),
      dispose: () => { conversationEventsDisposed = true; } };
  `)(...Object.values(dependencies)) as {
    refresh(): Promise<void>; event(chunk: AgentConversationEvent): Promise<void>; dispose(): void;
  };
  const record = (activeTurnId: string | null) => ({
    ownedId, remoteProfileId: 'workbox', activeTurnId,
    state: activeTurnId ? 'working' : 'suspended', suspended: !activeTurnId,
    pendingPermission: false, pendingInput: false
  });
  const presence = () => deriveSessionPresence({
    terminalState: row.state as 'live' | 'background',
    suspended: row.runtimeState === 'suspended',
    runtimeState: row.runtimeState as 'working' | 'ready' | 'suspended',
    activeTurnId: row.activeTurnId
  }, get(sessionPresenceHistory)[ownedId] ?? EMPTY_SESSION_PRESENCE_HISTORY, 100).state;
  const event = (sequence: number, turnId: string, turnState: string) => ({
    ownedId, provider: 'codex' as const, generation: 1, sequence, timestampMs: sequence * 10,
    payload: { kind: 'turn', state: turnState, turnId }
  });
  const reconnect = api.refresh();
  finishRead([record('remote-turn')]);
  await reconnect;
  assert.equal(presence(), 'working', 'reconnect reveals an already-running background turn');
  assert.equal(get(sessionPresenceHistory)[ownedId].turnStartedAt, null, 'inventory must not fabricate a turn start time');
  await api.event(event(1, 'remote-turn', 'completed'));
  assert.equal(row.activeTurnId, null, 'completion clears the inventory active-turn flag');
  assert.notEqual(presence(), 'working');
  row.runtimeState = 'suspended';
  await api.event(event(2, 'next-turn', 'started'));
  assert.equal(presence(), 'working', 'a live start overrides both cached suspension and old unread completion');
  assert.equal(opened, false, 'the user never needed to open this session');
  const racingRead = api.refresh();
  await api.event(event(3, 'next-turn', 'completed'));
  const rereading = new Promise<void>((resolve) => { readStarted = resolve; });
  finishRead([record('next-turn')]);
  await rereading;
  assert.equal(row.activeTurnId, null, 'an older inventory reply cannot revive a completed turn');
  finishRead([record(null)]);
  await racingRead;
  await api.event(event(2, 'next-turn', 'started'));
  assert.equal(row.activeTurnId, null, 'duplicate replay cannot revive a completed turn');
  await api.event(event(4, 'failed-turn', 'started'));
  await api.event({ ownedId, provider: 'codex', generation: 1, sequence: 5, timestampMs: 50,
    payload: { kind: 'error', code: 'test', message: 'Runtime ended', recoverable: false } });
  assert.equal(row.activeTurnId, null, 'runtime errors clear the rail turn as well as chat controls');
  assert.equal(row.runtimeState, 'failed');
  assert.equal(get(sessionPresenceHistory)[ownedId].activeTurnId, null);

  const firstRead = api.refresh();
  const resolveFirst = finishRead;
  const secondRead = api.refresh();
  finishRead([record(null)]);
  await secondRead;
  resolveFirst([record('obsolete-turn')]);
  await firstRead;
  assert.equal(row.activeTurnId, null, 'a superseded reconnect reply cannot restore old work');

  opened = true;
  state.activeTurnId = 'stale-turn';
  state.sending = true;
  const stale = api.refresh();
  finishRead([record(null)]);
  await stale;
  assert.equal(state.activeTurnId, undefined, 'live inventory clears stale chat Stop state');
  assert.equal(state.sending, false);

  const disconnected = api.refresh();
  dependencies.rail.remoteConnections.workbox = 'disconnected';
  finishRead([record('unknown-turn')]);
  await disconnected;
  assert.equal(row.activeTurnId, null, 'a reply after disconnect is not current runtime truth');
  dependencies.rail.remoteConnections.workbox = 'connected';
  const disposed = api.refresh();
  api.dispose();
  finishRead([record('late-turn')]);
  await disposed;
  assert.equal(row.activeTurnId, null, 'shutdown invalidates pending activity reads');
});

await test('an unchanged sidebar row update leaves the row list untouched', async () => {
  const railPath = fileURLToPath(new URL('../src/lib/shell/stores/sessionRailStore.svelte.ts', import.meta.url));
  const railOutput = fileURLToPath(new URL('./.sessionRailStore.test.mjs', import.meta.url));
  writeFileSync(railOutput, compileForTest(railPath, 'sessionRailStore.svelte.js'));
  let railStore;
  try {
    railStore = await import(`${railOutput}?test=${Date.now()}`);
  } finally {
    rmSync(railOutput, { force: true });
  }
  const backgroundWork = [{ id: 't1', kind: 'command', label: 'Task', startedAtMs: 1 }];
  railStore.hydrateOwned([{ ownedId: 'row-a', title: 'A', state: 'background', backgroundWork }]);
  const before = railStore.rail.owned;
  railStore.updateOwnedSession('row-a', { state: 'background', backgroundWork });
  assert.equal(railStore.rail.owned, before, 'a reconnect refresh with the same values must not re-render the rail');
  railStore.updateOwnedSession('row-a', { state: 'live' });
  assert.notEqual(railStore.rail.owned, before);
  assert.equal(railStore.rail.owned[0].state, 'live');
});

await test('selected refresh reports failure and releases buffered terminal events', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('function selectedEventUsesControlCursor'), source.indexOf('async function refreshSelectedConversation'));
  const code = stripTypeScriptTypes(block, { mode: 'strip' }).replace(/^export /gm, '');
  for (const aborted of [false, true]) {
    const errors: unknown[] = [];
    const delivered: number[] = [];
    let rejectRead: (error: Error) => void = () => {};
    const read = {
      ownedId: 'refresh-failure', generation: 1, pageWatermark: 10, pendingSequence: 15,
      reading: false, reloadRequested: false, events: [] as AgentConversationEvent[], chunks: [],
      bytes: 0, overflow: false, maxBytes: 1024, abortController: new AbortController(),
      onSnapshot: () => assert.fail('failed refresh must preserve the displayed snapshot'),
      onEvent: (event: AgentConversationEvent) => delivered.push(event.sequence),
      onError: (error: unknown) => errors.push(error)
    };
    const dependencies = {
      selectedConversationRead: read, childConversationRead: null, isCurrentConversationRead: (candidate: unknown) => candidate === read && !read.abortController.signal.aborted, ACTIVE_EVENT_WINDOW_EVENTS: 100,
      ACTIVE_EVENT_WINDOW_BYTES: 100_000,
      readAgentConversationSelectionFromTauri: () => new Promise((_resolve, reject) => { rejectRead = reject; })
    };
    const admit = Function(...Object.keys(dependencies), `${code}\nreturn admitSelectedConversationEvent;`)(...Object.values(dependencies)) as (event: AgentConversationEvent) => void;
    const event = (sequence: number): AgentConversationEvent => ({
      ownedId: read.ownedId, provider: 'codex', generation: 1, sequence, timestampMs: sequence,
      payload: { kind: 'turn', turnId: 'active-turn', state: 'completed' }
    });
    admit(event(16)); // Missing 11–15 requires an authoritative refresh.
    admit(event(17)); // The terminal arriving during the read must not settle the turn.
    if (aborted) read.abortController.abort();
    const failure = new Error('selected history read failed');
    rejectRead(failure);
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.deepEqual(delivered, []);
    assert.equal(read.pageWatermark, 10);
    assert.equal(read.reading, false);
    assert.deepEqual(errors, aborted ? [] : [failure], 'departed selections must not receive refresh errors');
    if (!aborted) {
      assert.equal(read.events.length, 0);
      assert.equal(read.bytes, 0);
    }
  }
});

await test('a session the backend has not started yet still reports its empty chat ready', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('function createConnection('), source.indexOf('export function selectConversationChat('));
  const enqueueFactory = () => {
    const items: unknown[] = [];
    let wake: (() => void) | null = null;
    return {
      push(item: unknown) { items.push(item); wake?.(); },
      async *stream() {
        for (;;) {
          if (items.length) yield items.shift();
          else await new Promise<void>((resolve) => { wake = resolve; });
        }
      }
    };
  };
  for (const started of [false, true]) {
    const dependencies = {
      enqueueFactory,
      PAGE_BYTES: 1024,
      EventType,
      // The backend answers null for a session with no row yet, so no snapshot is delivered.
      subscribeSelectedConversation: async (input: { onSnapshot: (snapshot: unknown) => void }) => {
        if (started) input.onSnapshot({ page: 'saved' });
        return () => {};
      },
      admitInitialSnapshot: (_selection: unknown, _snapshot: unknown, push: (item: unknown) => void) => push('admitted snapshot'),
      emitEvent: () => undefined
    };
    const createConnection = Function(...Object.keys(dependencies), `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn createConnection;`)(...Object.values(dependencies));
    const selection = { workspaceOwnedId: 'new-session', historyOwnedId: 'new-session', controller: new AbortController() };
    const stream = createConnection(selection).subscribe()[Symbol.asyncIterator]();
    const first = await Promise.race([
      stream.next().then((result: IteratorResult<unknown>) => result.value),
      new Promise((resolve) => setImmediate(() => setImmediate(() => resolve('nothing yielded'))))
    ]);
    if (started) assert.equal(first, 'admitted snapshot', 'a found session is admitted from its snapshot');
    else assert.deepEqual(first, { type: EventType.CUSTOM, name: 'assembly:snapshot-ready', value: 'new-session' });
    selection.controller.abort();
    await stream.return?.();
  }
});

await test('a sent message draws below the history it was sent after', () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('export async function sendSelectedConversationMessage('), source.indexOf('export async function refreshSelectedConversationChat('))
    .replace('export ', '');
  const processor = new StreamProcessor();
  processor.setMessages([{ id: 'user-earlier', role: 'user', parts: [{ type: 'text', content: 'Earlier' }],
    metadata: { itemType: 'user-message', completed: true, startedAtMs: Date.now() - 60_000 } }]);
  const active = {
    workspaceOwnedId: 'owned-sent',
    pendingAdmission: null,
    chat: {
      // The chat draws its own copy of the message the moment it is sent, as TanStack does.
      sendMessage(input: { content: any; metadata?: Record<string, unknown> }) {
        processor.addUserMessage(input.content, undefined, input.metadata);
        return new Promise(() => {});
      }
    }
  };
  const send = Function('active', 'rejectPendingAdmission',
    `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn sendSelectedConversationMessage;`)(active, () => {});
  void send('owned-sent', 'Next question');
  const shown = conversationDisplayItems(processor.getMessages());
  assert.deepEqual(shown.map((item) => item.kind === 'user' ? item.text : item.kind), ['Earlier', 'Next question'],
    'the sent copy is the newest row, so the send anchor scrolls to it rather than to the top of the history');
});

await test('a sent message draws its screenshots in the same step as its text', () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('export async function sendSelectedConversationMessage('), source.indexOf('export async function refreshSelectedConversationChat('))
    .replace('export ', '');
  const processor = new StreamProcessor();
  const active = {
    workspaceOwnedId: 'owned-shots',
    pendingAdmission: null,
    chat: {
      sendMessage(input: { id?: string; content: any; metadata?: Record<string, unknown> }) {
        processor.addUserMessage(input.content, input.id, input.metadata);
        return new Promise(() => {});
      }
    }
  };
  const send = Function('active', 'rejectPendingAdmission', 'attachmentDisplayMetadata',
    `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn sendSelectedConversationMessage;`
  )(active, () => {}, ({ bytes: _bytes, ...shown }: Record<string, unknown>) => shown);
  void send('owned-shots', 'Look at this', 'user-optimistic', [
    { id: 'shot', name: 'shot.png', mimeType: 'image/png', path: '/vault/shot.png', previewUrl: 'blob:shot', bytes: [1, 2, 3] }
  ]);
  const [shown] = conversationDisplayItems(processor.getMessages());
  assert.equal(shown.itemId, 'user-optimistic', 'the surface knows the row it will scroll to');
  assert.equal(shown.kind, 'user');
  const attachments = shown.kind === 'user' ? shown.attachments ?? [] : [];
  assert.deepEqual(attachments.map((item) => item.previewUrl), ['blob:shot'],
    'the thumbnail draws with the text, before any backend receipt');
  assert.equal('bytes' in attachments[0], false, 'the transcript copy carries no image bytes');
});

await test('displayed child state is isolated and eviction preserves parent controls and previews', () => {
  store.ensureConversationSession('drill-parent', 'codex');
  store.ensureConversationSession('drill-child-history', 'codex');
  const parent = store.getConversationSession('drill-parent');
  const child = store.getConversationSession('drill-child-history');
  parent.selectedChildHistoryOwnedId = child.ownedId;
  parent.selectedBeforeCursor = 900;
  parent.sentAttachments = { parentMessage: [{ previewUrl: 'blob:parent-preview' }] };
  child.sentAttachments = { childMessage: [{ previewUrl: 'blob:child-preview' }] };
  const revoked: string[] = [];
  const originalRevoke = URL.revokeObjectURL;
  URL.revokeObjectURL = (url) => { revoked.push(url); };
  try {
    store.applySelectedConversationSnapshotState(child.ownedId, child.ownedId, {
      connection: { provider: 'codex', generation: 7 }, suspended: false, activeTurnId: 'child-live',
      page: { turns: [{ turnId: 'child-turn' }], beforeCursor: 10, afterCursor: 20, hasBefore: true,
        hasAfter: false, watermark: 20, transferBytes: 200, oversized: false }
    }, false);
    assert.equal(child.generation, 7);
    assert.equal(child.activeTurnId, 'child-live');
    const childTurnEvent = (state: string, turnId: string) => ({
      ownedId: child.ownedId, provider: 'codex', generation: 7, sequence: 21, timestampMs: 21,
      payload: { kind: 'turn', state, turnId }
    });
    store.applySelectedConversationHistoryEventState(child.ownedId, childTurnEvent('started', 'child-next'));
    assert.equal(child.activeTurnId, 'child-next');
    store.applySelectedConversationHistoryEventState(child.ownedId, childTurnEvent('completed', 'older-child'));
    assert.equal(child.activeTurnId, 'child-next', 'an unrelated terminal cannot finish the current child turn');
    store.applySelectedConversationHistoryEventState(child.ownedId, childTurnEvent('completed', 'child-next'));
    assert.equal(child.activeTurnId, undefined);
    assert.equal(parent.activeTurnId, undefined, 'child activity cannot start a parent turn');
    store.applySelectedConversationHistoryEventState(child.ownedId, childTurnEvent('started', 'child-error'));
    store.applySelectedConversationHistoryEventState(child.ownedId, {
      ...childTurnEvent('started', 'child-error'), payload: { kind: 'error', message: 'child failed' }
    });
    assert.equal(child.activeTurnId, undefined);
    assert.equal(child.selectedBeforeCursor, 10);
    assert.equal(parent.selectedBeforeCursor, 900);
    store.evictInactiveConversationSessions(parent.ownedId);
    assert.equal(store.getConversationSession(child.ownedId), child);
    assert.equal(revoked.includes('blob:parent-preview'), false);
    assert.equal(revoked.includes('blob:child-preview'), false);
    parent.selectedChildHistoryOwnedId = null;
    store.evictConversationSession(child.ownedId);
    assert.equal(store.getConversationSession(child.ownedId), null);
    assert.equal(store.getConversationSession(parent.ownedId), parent);
    assert.deepEqual(Object.keys(parent.sentAttachments), ['parentMessage']);
    assert.equal(revoked.includes('blob:child-preview'), true);
    assert.equal(revoked.includes('blob:parent-preview'), false);
  } finally {
    store.evictConversationSession(parent.ownedId);
    URL.revokeObjectURL = originalRevoke;
  }
});

await test('leaving a session ends every wait on its stream and readiness and writes nothing after', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const between = (start: string, end: string) => source.slice(source.indexOf(start), source.indexOf(end));
  const block = [
    between('function isCurrent(', 'function messageBytes('),
    between('function enqueueFactory(', 'async function restorePageAttachments('),
    between('function rejectPendingAdmission(', 'export function selectConversationChat('),
    between('export function selectConversationChat(', 'export async function pageSelectedConversation(')
  ].join('\n').replaceAll('export ', '');
  let aborted = false;
  const writesAfterLeave: string[] = [];
  const write = (name: string) => () => { if (aborted) writesAfterLeave.push(name); };
  const stopped: number[] = [];
  let subscriptions = 0;
  let connection: any;
  const session = { provider: 'claude', generation: 1, selectedHasAfter: true, selectedChildHistoryOwnedId: null };
  const dependencies = {
    takeStreamItem: (items: unknown[]) => items.shift(),
    recordLiveMessages: write('live messages'),
    EventType,
    PAGE_BYTES: 1024,
    getConversationSession: () => session,
    ensureConversationSession: () => session,
    evictConversationSession: () => undefined,
    restoreSelectedConversationAttachments: () => undefined, closeAguiViewInBackground: () => undefined,
    subscribeSelectedConversation: async () => {
      const id = ++subscriptions;
      return () => { stopped.push(id); };
    },
    admitInitialSnapshot: write('snapshot'),
    emitEvent: write('event'),
    pushMessagesSnapshot: write('messages snapshot'),
    optimisticUserId: () => null,
    sendStructuredMessage: async () => { writesAfterLeave.push('native send'); return null; },
    setConversationSendError: write('send error'),
    failChildConversationTranscript: write('child error'),
    attachmentDisplayMetadata: (attachment: unknown) => attachment,
    createChat: (options: { connection: any }) => {
      connection = options.connection;
      return {
        messages: [],
        dispose: () => undefined,
        // As TanStack does, a send hands the message to the connection and waits for it.
        async sendMessage() {
          await connection.send([{ role: 'user', parts: [{ type: 'text', content: 'Hello' }] }], undefined, undefined, { runId: 'run-1' });
        }
      };
    }
  };
  const api = Function(...Object.keys(dependencies), `${stripTypeScriptTypes(block, { mode: 'strip' })}
    let active = null;
    let childActive = null;
    return { selectConversationChat, sendSelectedConversationMessage, refreshSelectedConversationChat, selectedConversationChatReady };
  `)(...Object.values(dependencies));
  const settles = (promise: Promise<unknown>) => Promise.race([
    promise.then((value) => ({ value }), (error) => ({ error })),
    new Promise((resolve) => setTimeout(() => resolve('still waiting'), 200))
  ]);
  const pending = (promise: Promise<unknown>) => Promise.race([
    promise.then(() => false, () => false),
    new Promise((resolve) => setImmediate(() => setImmediate(() => resolve(true))))
  ]);

  const owner = new AbortController();
  api.selectConversationChat('leave-me', 'leave-me', owner.signal);
  const stream = connection.subscribe()[Symbol.asyncIterator]();
  assert.equal((await stream.next()).value.name, 'assembly:snapshot-ready', 'the stream is open');
  const admission = api.sendSelectedConversationMessage('leave-me', 'Hello');
  const refresh = api.refreshSelectedConversationChat('leave-me');
  const ready = api.selectedConversationChatReady('leave-me');
  // Drain what the opens queued; the last read then waits for more.
  let next = stream.next();
  while (!(await pending(next))) next = stream.next();
  assert.equal(await pending(admission), true, 'the send waits for the reloaded chat to be ready');
  assert.equal(await pending(ready), true);

  aborted = true;
  owner.abort();
  assert.deepEqual(await settles(next), { value: { value: undefined, done: true } }, 'the stream ends instead of waiting');
  for (const waiting of [admission, refresh, ready]) {
    const settled = await settles(waiting) as { error?: DOMException };
    assert.equal(settled.error?.name, 'AbortError', 'every readiness wait ends when the session is left');
  }
  assert.ok(stopped.includes(subscriptions), 'the native watch is stopped');
  assert.deepEqual(writesAfterLeave, [], 'nothing is sent or written after leaving');
});


test('history_download_progress_is_isolated_by_workspace_and_child', () => {
  store.ensureConversationSession('download-root', 'codex');
  const root = store.getConversationSession('download-root');
  const other = store.ensureConversationSession('download-other', 'codex');
  const progress = { profileId: 'p', sourceOwnedId: 'download-root', workspaceOwnedId: 'download-root', childSessionId: null,
    totalBytes: 100, coveredBytes: 10, phase: 'downloading', ready: false, error: null };
  store.setConversationHistoryDownload('download-root', progress);
  assert.equal(root.historyDownload.coveredBytes, 10);
  assert.equal(other.historyDownload, null);
  root.selectedChildId = 'child';
  store.setConversationHistoryDownload('download-root', { ...progress, sourceOwnedId: 'source-child', childSessionId: 'child' });
  assert.equal(root.childHistoryDownload.sourceOwnedId, 'source-child');
  store.setConversationHistoryDownload('download-root', { ...progress, childSessionId: 'another', coveredBytes: 90 });
  assert.equal(root.childHistoryDownload.coveredBytes, 10);
});

test('history_download_progress_from_a_released_selection_is_ignored', () => {
  const root = store.ensureConversationSession('download-released', 'codex');
  root.selectedChildId = 'new-child';
  store.setConversationHistoryDownload('download-released', { profileId: 'p', sourceOwnedId: 'old-source', workspaceOwnedId: 'download-released', childSessionId: 'old-child',
    totalBytes: 100, coveredBytes: 10, phase: 'downloading', ready: false, error: null });
  assert.equal(root.childHistoryDownload, null);
  store.setConversationHistoryDownload('download-released', { profileId: 'p', sourceOwnedId: 'wrong-root', workspaceOwnedId: 'wrong-root', childSessionId: null,
    totalBytes: 100, coveredBytes: 10, phase: 'downloading', ready: false, error: null });
  assert.equal(root.historyDownload, null);
});
