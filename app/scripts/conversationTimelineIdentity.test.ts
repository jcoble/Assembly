import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import {
  reuseConversationDisplayItems,
  type ConversationDisplayItem
} from '../src/lib/shell/conversation/conversationTimeline.ts';
import { conversationDisplayItems, takeStreamItem } from '../src/lib/shell/conversation/conversationMessages.ts';
import { EventType, type UIMessage } from '@tanstack/ai/client';

const items: UIMessage[] = Array.from({ length: 2_000 }, (_, index) => ({
  id: `item-${index}`,
  role: 'assistant',
  parts: [{ type: 'text', content: `Message ${index}` }],
  metadata: { itemType: 'assistant-message', completed: index < 1_999, startedAtMs: index }
}));

const before = conversationDisplayItems(items);
const nextItems = items.slice();
nextItems[1_999] = {
  ...items[1_999],
  parts: [{ type: 'text', content: 'Message 1999 plus one delta' }]
};
const after = conversationDisplayItems(nextItems, before);

const changed = after.filter((item: ConversationDisplayItem, index: number) => item !== before[index]);
assert.equal(changed.length, 1, 'one delta changes only the touched display item object');
assert.equal(changed[0].itemId, 'item-1999');
for (let index = 0; index < 1_999; index += 1) {
  assert.equal(after[index], before[index], `display item ${index} keeps object identity`);
}

test('unchanged_messages_keep_their_display_items', () => {
  const first = conversationDisplayItems(items);
  const second = conversationDisplayItems(items);
  assert.ok(first.every((item, index) => item === second[index]));
});

test('a_plan_with_equal_steps_is_the_same_display_item', () => {
  const steps = () => [{ id: 'step-1', title: 'Read', detail: null, state: 'in-progress' as const, ownerAgentId: null, startedAt: null, completedAt: null }];
  const plan = { kind: 'plan' as const, itemId: 'plan-1', title: 'Plan', steps: steps(), timestampMs: 1 };
  const [reused] = reuseConversationDisplayItems([{ ...plan, steps: steps() }], [plan]);
  assert.equal(reused, plan);
});

test('streamed_text_deltas_in_one_frame_reach_the_chat_as_one_update', async () => {
  let frames = 0;
  Object.assign(globalThis, {
    requestAnimationFrame: (callback: () => void) => { frames += 1; return setTimeout(callback, 0); },
    cancelAnimationFrame: (frame: ReturnType<typeof setTimeout>) => clearTimeout(frame)
  });
  const delta = (messageId: string, text: string) => ({ type: EventType.TEXT_MESSAGE_CONTENT, messageId, delta: text });
  const other = { type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'b', delta: 'Other' };
  const finished = { type: EventType.RUN_FINISHED, threadId: 'owner', runId: 'run-1' };
  const queue: object[] = [delta('a', 'One'), delta('a', ' two'), delta('a', ' three'), other, finished, delta('a', ' four')];
  const signal = new AbortController().signal;
  assert.deepEqual(await takeStreamItem(queue, signal), delta('a', 'One two three'));
  assert.equal(frames, 1);
  assert.deepEqual(await takeStreamItem(queue, signal), other, 'another message waits for its own update');
  assert.equal(await takeStreamItem(queue, signal), finished);
  assert.deepEqual(await takeStreamItem(queue, signal), delta('a', ' four'));
  assert.equal(queue.length, 0);
});

test('a_push_while_the_stream_takes_from_an_empty_queue_is_delivered', async () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const block = source.slice(source.indexOf('function enqueueFactory('), source.indexOf('function resetReady('));
  const enqueueFactory = Function('recordLiveMessages', 'takeStreamItem',
    `${stripTypeScriptTypes(block, { mode: 'strip' })}\nreturn enqueueFactory;`
  )(() => undefined, takeStreamItem);
  const queue = enqueueFactory(new AbortController().signal, { chat: { messages: [] } });
  const next = queue.stream()[Symbol.asyncIterator]().next();
  queue.push({ type: 'probe', sequence: 7 });
  const delivered = await Promise.race([next, new Promise((resolve) => setTimeout(() => resolve('still waiting'), 200))]);
  assert.deepEqual(delivered, { value: { type: 'probe', sequence: 7 }, done: false });
});

console.log('conversation timeline identity tests passed');
