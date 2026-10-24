import assert from 'node:assert/strict';
import { EventType, StreamProcessor } from '@tanstack/ai/client';

import {
  createConversationState,
  reduceConversationEvent
} from '../src/lib/shell/conversation/conversationReducer.ts';
import { conversationDisplayItems } from '../src/lib/shell/conversation/conversationMessages.ts';
import type { AgentConversationEvent } from '../src/lib/shell/conversation/conversationTypes.ts';
import { configOptionPlacement } from '../src/lib/shell/conversation/conversationTypes.ts';
import { decideConversationActivation } from '../src/lib/shell/conversation/conversationActivation.ts';

const event = (overrides: Partial<AgentConversationEvent> = {}): AgentConversationEvent => ({
  ownedId: 'owned-a',
  provider: 'codex',
  generation: 1,
  sequence: 1,
  timestampMs: 1_000,
  payload: { kind: 'connection', state: 'connected', nativeSessionId: 'thread-a' },
  ...overrides
});

// The chat's messages from the chunks the backend sends: a reply, and a row AG-UI has no event for.
const fold = (chunks: object[]) => {
  const processor = new StreamProcessor();
  for (const chunk of chunks) processor.processChunk(chunk as never);
  return processor.getMessages();
};
const reply = (id: string, text: string, startedAtMs: number, turnId?: string) => [
  { type: EventType.TEXT_MESSAGE_START, messageId: id, role: 'assistant',
    metadata: { itemType: 'assistant-message', turnId, startedAtMs, firstSequence: startedAtMs, completed: false } },
  { type: EventType.TEXT_MESSAGE_CONTENT, messageId: id, delta: text },
  { type: EventType.TEXT_MESSAGE_END, messageId: id, metadata: { completed: true } }
];
const row = (id: string, native: AgentConversationEvent) =>
  ({ type: EventType.ACTIVITY_SNAPSHOT, messageId: id, activityType: `assembly.${native.payload.kind}`, content: { event: native } });

// The first event establishes the live generation and sequence.
{
  const state = reduceConversationEvent(createConversationState('owned-a', 'codex'), event());
  assert.equal(state.generation, 1);
  assert.equal(state.lastSequence, 1);
  assert.equal(state.connectionState, 'connected');
}

// Older generations and duplicate sequences are ignored by identity.
{
  const current = reduceConversationEvent(createConversationState('owned-a', 'codex'), event());
  const older = reduceConversationEvent(current, event({ generation: 0, sequence: 99 }));
  const duplicate = reduceConversationEvent(current, event({ sequence: 1 }));
  assert.equal(older, current);
  assert.equal(duplicate, current);
}

// A missing event is never guessed. Existing history remains visible.
{
  const first = event();
  const user = event({
    sequence: 2,
    payload: { kind: 'userMessage', itemId: 'user-1', text: 'Keep this', completed: true }
  });
  let state = reduceConversationEvent(createConversationState('owned-a', 'codex'), first);
  state = reduceConversationEvent(state, user);
  state = reduceConversationEvent(state, event({
    sequence: 4,
    payload: { kind: 'assistantDelta', itemId: 'assistant-1', delta: 'Missing sequence three' }
  }));
  assert.equal(state.desynchronized, true);
}

// An admitted prompt uses native time so it follows every row of the previous turn.
{
  const processor = new StreamProcessor();
  processor.setMessages([...fold([...reply('assistant-a1', 'First', 100, 'turn-a'), ...reply('assistant-a2', 'Second', 200, 'turn-a')]), {
    id: 'user-b', role: 'user', parts: [{ type: 'text', content: 'next' }],
    metadata: { itemType: 'user-message', startedAtMs: 150 }
  }]);
  // The backend records the chat's message under the chat's id, with the turn and time it was admitted.
  for (const chunk of [
    { type: EventType.TEXT_MESSAGE_START, messageId: 'user-b', role: 'user',
      metadata: { itemType: 'user-message', turnId: 'turn-b', startedAtMs: 300, firstSequence: 3 } },
    { type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'user-b', delta: 'next' },
    { type: EventType.TEXT_MESSAGE_END, messageId: 'user-b', metadata: { completed: true } }
  ]) processor.processChunk(chunk as never);
  assert.equal(conversationDisplayItems(processor.getMessages()).at(-1)?.text, 'next', 'the text is not repeated');
  assert.deepEqual(conversationDisplayItems(processor.getMessages()).map((item) => [item.turnId, item.itemId]), [
    ['turn-a', 'assistant-a1'], ['turn-a', 'assistant-a2'], ['turn-b', 'user-b']
  ]);
}

// Each plan update is a row; the newest says where the plan stands.
{
  const events = [event({
    sequence: 2,
    payload: {
      kind: 'plan',
      items: [{ text: 'Inspect the change', status: 'pending' }]
    }
  }), event({
    sequence: 3,
    payload: {
      kind: 'plan',
      items: [{ text: 'Inspect the change', status: 'completed' }]
    }
  })];
  const plan = conversationDisplayItems(fold(events.map((native) => row(`plan:${native.sequence}`, native)))).at(-1);
  assert.equal(plan?.kind, 'plan');
  assert.deepEqual(plan?.steps.map((step) => ({ title: step.title, state: step.state })), [{ title: 'Inspect the change', state: 'completed' }]);
}

// A connected structured runtime is a pure view switch. Only a stopped,
// native-id-bearing external Codex session may cross from terminal ownership
// into a structured session/load activation.
{
  const appSession = {
    agent: 'codex', origin: 'app', state: 'background',
    executionOwner: 'structured', ptySessionId: null, nativeSessionId: 'thread-app'
  };
  assert.deepEqual(decideConversationActivation(appSession, {
    provider: 'codex', state: 'connected', nativeSessionId: 'thread-app'
  }), { kind: 'view' });
  assert.deepEqual(decideConversationActivation(appSession, null), {
    kind: 'structured', nativeSessionMode: 'resume'
  });

  const stoppedExternal = {
    agent: 'codex', origin: 'external', state: 'background',
    executionOwner: 'stopped', ptySessionId: null, nativeSessionId: 'thread-cli'
  };
  assert.deepEqual(decideConversationActivation(stoppedExternal, null), {
    kind: 'structured', nativeSessionMode: 'load'
  });
  assert.deepEqual(decideConversationActivation(stoppedExternal, {
    provider: 'codex', state: 'connected', nativeSessionId: 'thread-cli'
  }), {
    kind: 'structured', nativeSessionMode: 'load'
  });
  assert.deepEqual(decideConversationActivation({
    ...stoppedExternal, executionOwner: 'terminal', ptySessionId: 'pty-live'
  }, {
    provider: 'codex', state: 'connected', nativeSessionId: 'thread-cli'
  }), { kind: 'terminal' });
  assert.deepEqual(decideConversationActivation({
    ...stoppedExternal, nativeSessionId: null
  }, null), { kind: 'terminal' });
  assert.deepEqual(decideConversationActivation({
    ...stoppedExternal, agent: 'other'
  }, null), { kind: 'terminal' });
}

// A provider error is additive and does not clear completed messages.
{
  const error = event({ sequence: 2, timestampMs: 2_000, payload: { kind: 'error', code: 'connection_lost', message: 'Reconnect', recoverable: true } });
  const displayed = conversationDisplayItems(fold([...reply('assistant-1', 'Still here', 1_000), row('error:1:2', error)]));
  assert.equal(displayed[0].text, 'Still here');
  assert.equal(displayed[1].kind, 'error');
  assert.equal(displayed[1].metadata?.recoverable, true);
}

// Known categories get their fixed controls and future categories remain generic.
{
  assert.equal(configOptionPlacement('model'), 'model-picker');
  assert.equal(configOptionPlacement('thought_level'), 'reasoning-picker');
  assert.equal(configOptionPlacement('mode'), 'mode-picker');
  assert.equal(configOptionPlacement('model_config'), 'model-popover');
  assert.equal(configOptionPlacement('provider.future/category'), 'more-options');
}

console.log('agent conversation protocol tests passed');
