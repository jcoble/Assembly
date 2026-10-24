/// <reference types="node" />
// TSK-1167: one chat turn as the frontend sees it, from the prompt to the
// turn's end, for Claude Code and Codex alike. Each test pins behaviour that
// works today so a change that breaks it fails here.

import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { compileModule } from 'svelte/compiler';
import { EventType, StreamProcessor } from '@tanstack/ai/client';
import {
  createConversationState,
  reduceConversationEvent,
  shouldClearConversationSending
} from '../src/lib/shell/conversation/conversationReducer.ts';
import { conversationDisplayItems, conversationHasRunningTool } from '../src/lib/shell/conversation/conversationMessages.ts';
import { foldFileEdits, turnFileChanges } from '../src/lib/shell/conversation/conversationTimeline.ts';
import type { AgentConversationEvent } from '../src/lib/shell/conversation/conversationTypes.ts';

// The store reads `import.meta.env` and a runes module, so it is compiled the
// same way agentConversationStore.test.ts compiles it.
const at = (path: string): string => fileURLToPath(new URL(path, import.meta.url));
const compile = (path: string, filename: string): string => compileModule(
  stripTypeScriptTypes(readFileSync(path, 'utf8'), { mode: 'strip' }),
  { generate: 'client', filename }
).js.code.replaceAll('import.meta.env', '({ DEV: false })');
const storeOut = at('../src/lib/shell/conversation/.conversationTurnStream.test.mjs');
const diagnosticsOut = at('../src/lib/shell/.conversationTurnStreamDiagnostics.test.mjs');
writeFileSync(storeOut, compile(at('../src/lib/shell/conversation/conversationStore.svelte.ts'), 'conversationStore.svelte.js')
  .replace(/\.\.\/resourceDiagnostics\.svelte(\.ts|\.js)/, '../.conversationTurnStreamDiagnostics.test.mjs'));
writeFileSync(diagnosticsOut, compile(at('../src/lib/shell/resourceDiagnostics.svelte.ts'), 'resourceDiagnostics.svelte.js'));
let store;
try {
  store = await import(`${storeOut}?test=${Date.now()}`);
} finally {
  rmSync(storeOut, { force: true });
  rmSync(diagnosticsOut, { force: true });
}

type Provider = 'codex' | 'claude';
const PROVIDERS: Provider[] = ['codex', 'claude'];

function stream(ownedId: string, provider: Provider) {
  let sequence = 0;
  return (payload: Record<string, unknown>, extra: Partial<AgentConversationEvent> = {}): AgentConversationEvent => ({
    ownedId, provider, generation: 1, sequence: ++sequence, timestampMs: sequence * 10,
    payload: payload as AgentConversationEvent['payload'], ...extra
  });
}

// Protects 50ca299a7 (app-minted turns): the composer's Working state ends on
// the turn's end or an error, and on nothing else.
test('Working clears when a turn ends or fails, and stays through the turn\'s own events', () => {
  const event = stream('owned-working', 'codex');
  for (const state of ['completed', 'interrupted', 'failed']) {
    assert.equal(shouldClearConversationSending(event({ kind: 'turn', turnId: 't', state })), true, state);
  }
  assert.equal(shouldClearConversationSending(event({ kind: 'error', code: 'x', message: 'Lost', recoverable: true })), true);
  for (const payload of [
    { kind: 'turn', turnId: 't', state: 'started' },
    { kind: 'userMessage', itemId: 'u', text: 'Hi', completed: true },
    { kind: 'assistantDelta', itemId: 'a', delta: 'Hel' },
    { kind: 'tool', itemId: 'tool', name: 'Read', state: 'started' },
    { kind: 'approval', requestId: 'r', state: 'requested', summary: 'Edit' }
  ]) assert.equal(shouldClearConversationSending(event(payload)), false, String(payload.kind));
  for (const type of ['turn.completed', 'turn.interrupted', 'runtime.error']) {
    assert.equal(shouldClearConversationSending({ type } as never), true, type);
  }
  assert.equal(shouldClearConversationSending({ type: 'content.delta' } as never), false);
});

test('the active turn is set by its start and cleared by its end or an error, for both providers', () => {
  for (const provider of PROVIDERS) {
    for (const end of [
      { kind: 'turn', turnId: 'turn-1', state: 'completed' },
      { kind: 'turn', turnId: 'turn-1', state: 'interrupted' },
      { kind: 'error', code: 'x', message: 'Lost', recoverable: true }
    ]) {
      const event = stream(`owned-${provider}`, provider);
      let state = reduceConversationEvent(createConversationState(`owned-${provider}`, provider),
        event({ kind: 'connection', state: 'connected', nativeSessionId: 'native' }));
      state = reduceConversationEvent(state, event({ kind: 'turn', turnId: 'turn-1', state: 'started' }));
      assert.equal(state.activeTurnId, 'turn-1');
      state = reduceConversationEvent(state, event({ kind: 'assistantDelta', itemId: 'a', delta: 'Hi' }));
      assert.equal(state.activeTurnId, 'turn-1', 'a streamed reply does not end the turn');
      state = reduceConversationEvent(state, event(end));
      assert.equal(state.activeTurnId, undefined, `${provider} ${JSON.stringify(end)}`);
      assert.equal(state.connectionState, 'connected');
    }
  }
});

test('only an error the agent cannot recover from marks the connection failed', () => {
  const event = stream('owned-errors', 'claude');
  const connected = reduceConversationEvent(createConversationState('owned-errors', 'claude'),
    event({ kind: 'connection', state: 'connected' }));
  const fatal = reduceConversationEvent(connected, event({ kind: 'error', code: 'exit', message: 'Gone', recoverable: false }));
  assert.equal(fatal.connectionState, 'failed');
});

// Protects 270b09b46: a replaced session continues the event sequence, so the
// replacement is in step rather than reported as missing events.
test('a replacement session that continues the sequence stays in sync, and a gap is never applied', () => {
  const event = stream('owned-replace', 'codex');
  let state = reduceConversationEvent(createConversationState('owned-replace', 'codex'), event({ kind: 'connection', state: 'connected' }));
  state = reduceConversationEvent(state, event({ kind: 'turn', turnId: 'turn-1', state: 'started' }));
  state = reduceConversationEvent(state, event({ kind: 'connection', state: 'connected' }, { generation: 2 }));
  assert.equal(state.generation, 2);
  assert.equal(state.desynchronized, false);
  const skipped = reduceConversationEvent(state, { ...event({ kind: 'turn', turnId: 'turn-1', state: 'completed' }, { generation: 2 }), sequence: state.lastSequence + 2 });
  assert.equal(skipped.desynchronized, true);
  assert.equal(skipped.activeTurnId, 'turn-1', 'an event after a gap waits for the reload instead of being guessed at');
});

test('a usage report that carries only some numbers keeps the others', () => {
  const event = stream('owned-usage', 'codex');
  let state = reduceConversationEvent(createConversationState('owned-usage', 'codex'), event({ kind: 'connection', state: 'connected' }));
  state = reduceConversationEvent(state, event({ kind: 'usage', usedTokens: 1_200, contextWindow: 200_000 }));
  state = reduceConversationEvent(state, event({ kind: 'usage', usedTokens: 2_400 }));
  assert.deepEqual(state.usage, { inputTokens: undefined, outputTokens: undefined, usedTokens: 2_400, contextWindow: 200_000, totalTokens: undefined });
});

// The backend sends a call as a message of its own, then the native call as its result.
test('a tool call runs until its result, and the row shows the state and output it ended with', () => {
  for (const end of ['completed', 'failed']) {
    const processor = new StreamProcessor();
    const send = (chunks: object[]) => chunks.forEach((chunk) => processor.processChunk(chunk as never));
    send([
      { type: EventType.RUN_STARTED, threadId: 'owned-tool', runId: 'run-1' },
      { type: EventType.TOOL_CALL_START, toolCallId: 'tool-1', toolCallName: 'Run tests', parentMessageId: 'tool-1',
        metadata: { itemType: 'mcp-tool', turnId: 'turn-1', startedAtMs: 10, firstSequence: 1 } },
      { type: EventType.TOOL_CALL_ARGS, toolCallId: 'tool-1', delta: '{"summary":"npm test"}' }
    ]);
    assert.equal(conversationHasRunningTool(processor.getMessages()), true);
    assert.deepEqual(conversationDisplayItems(processor.getMessages()).map((item) => [item.kind, item.state, item.summary]),
      [['tool', 'running', 'npm test']]);
    const call = { kind: 'tool', itemId: 'tool-1', name: 'Run tests', state: end, summary: 'npm test', output: 'one\ntwo\nend' };
    send([
      { type: EventType.TOOL_CALL_END, toolCallId: 'tool-1', input: { summary: 'npm test' } },
      { type: EventType.TOOL_CALL_RESULT, toolCallId: 'tool-1', messageId: 'tool-1-result', role: 'tool', content: JSON.stringify(call),
        ...(end === 'failed' ? { metadata: { tanstack: { state: 'output-error' } } } : {}) }
    ]);
    assert.equal(conversationHasRunningTool(processor.getMessages()), false, end);
    assert.deepEqual(conversationDisplayItems(processor.getMessages()).map((item) => [item.kind, item.state, item.output, item.timestampMs]),
      [['tool', end, 'one\ntwo\nend', 10]]);
  }
});

// TSK-1574: while a call runs its result carries the output so far; the finished result keeps only
// what the closed row shows, and opening the row reads the output and diff by id.
test('a running call shows its streamed output and the finished row keeps no output', () => {
  const processor = new StreamProcessor();
  const send = (chunks: object[]) => chunks.forEach((chunk) => processor.processChunk(chunk as never));
  const result = (call: object) => ({ type: EventType.TOOL_CALL_RESULT, toolCallId: 'run', messageId: 'run-result', role: 'tool',
    content: JSON.stringify({ kind: 'tool', itemId: 'run', name: 'Bash', summary: 'seq 3', ...call }) });
  send([
    { type: EventType.RUN_STARTED, threadId: 'owned-lazy-tool', runId: 'run-1' },
    { type: EventType.TOOL_CALL_START, toolCallId: 'run', toolCallName: 'Bash', parentMessageId: 'run', metadata: { startedAtMs: 10 } },
    { type: EventType.TOOL_CALL_ARGS, toolCallId: 'run', delta: '{"summary":"seq 3"}' },
    result({ state: 'updated', output: '1\n2\n' })
  ]);
  assert.equal(conversationHasRunningTool(processor.getMessages()), true);
  assert.deepEqual(conversationDisplayItems(processor.getMessages()).map((item) => [item.state, item.output]), [['running', '1\n2\n']]);
  send([{ type: EventType.TOOL_CALL_END, toolCallId: 'run', input: { summary: 'seq 3' } }, result({ state: 'completed', hasDetail: true })]);
  assert.equal(conversationHasRunningTool(processor.getMessages()), false);
  assert.deepEqual(conversationDisplayItems(processor.getMessages()).map((item) => item.kind === 'tool' && [item.state, item.output, item.hasDetail, item.summary]),
    [['completed', undefined, true, 'seq 3']]);
});

test('file edits count lines from added and removed when the diff is not loaded', () => {
  const processor = new StreamProcessor();
  // What a history page sends for an edit: the path and the counts, no diff.
  [{ type: EventType.TOOL_CALL_START, toolCallId: 'edit', toolCallName: 'apply_patch', parentMessageId: 'edit' },
    { type: EventType.TOOL_CALL_END, toolCallId: 'edit', input: { path: 'notes.txt' } },
    { type: EventType.TOOL_CALL_RESULT, toolCallId: 'edit', messageId: 'edit-result', role: 'tool', content: JSON.stringify(
      { kind: 'tool', itemId: 'edit', name: 'apply_patch', state: 'completed', path: 'notes.txt', hasDetail: true, added: 2, removed: 1 }) }
  ].forEach((chunk) => processor.processChunk(chunk as never));
  const items = conversationDisplayItems(processor.getMessages());
  assert.deepEqual(turnFileChanges(items), { files: 1, added: 2, removed: 1 });
  assert.deepEqual(foldFileEdits(items).flatMap((item) => item.kind === 'fileEdits' ? item.edits : []), [{ itemId: 'edit', path: 'notes.txt', added: 2, removed: 1 }]);
});

// Protects 882f6c3b7 (ACP steering): a correction sent into a running turn
// joins that turn; only Stop ends it, and the turn records that it was stopped.
test('a steered message keeps the turn running and Stop records an interrupted turn', () => {
  for (const provider of PROVIDERS) {
    const ownedId = `owned-steer-${provider}`;
    store.ensureConversationSession(ownedId, provider);
    const event = stream(ownedId, provider);
    store.applySelectedConversationEventState(ownedId, event({ kind: 'connection', state: 'connected' }));
    store.applySelectedConversationEventState(ownedId, event({ kind: 'turn', turnId: 'turn-1', state: 'started' }));
    store.applySelectedConversationEventState(ownedId, event({ kind: 'userMessage', itemId: 'user-steer-1', text: 'Use the other file', completed: true }, { turnId: 'turn-1' }));
    assert.equal(store.getConversationSession(ownedId).activeTurnId, 'turn-1');
    store.applySelectedConversationEventState(ownedId, event({ kind: 'turn', turnId: 'turn-1', state: 'interrupted' }));
    const current = store.getConversationSession(ownedId);
    assert.equal(current.activeTurnId, undefined);
    assert.deepEqual(current.selectedTurns, [{ turnId: 'turn-1', startedAtMs: 20, endedAtMs: 40, terminalState: 'interrupted' }]);
  }
});

test('reading history back, an older turn\'s end does not stop the turn running now', () => {
  store.ensureConversationSession('owned-history', 'codex');
  const event = stream('owned-history', 'codex');
  store.applySelectedConversationHistoryEventState('owned-history', event({ kind: 'turn', turnId: 'live', state: 'started' }));
  store.applySelectedConversationHistoryEventState('owned-history', event({ kind: 'turn', turnId: 'older', state: 'completed' }));
  assert.equal(store.getConversationSession('owned-history').activeTurnId, 'live');
  store.applySelectedConversationHistoryEventState('owned-history', event({ kind: 'turn', turnId: 'live', state: 'completed' }));
  assert.equal(store.getConversationSession('owned-history').activeTurnId, undefined);
});
