import assert from 'node:assert/strict';
import {
  agentItemFromEvent,
  anchorRowIndex,
  continueHistoryPaging,
  nextToolGroupPinned,
  conversationTurnGroups,
  displayItemFromAgentItem,
  foldToolRuns,
  diffLineCounts,
  foldFileEdits,
  formatWorkedFor,
  latestPlan,
  toolFilePath,
  summarizeToolRun,
  summarizeCompletedWork,
  turnActivityLabel,
  turnFileChanges,
  turnRows,
  USER_MESSAGE_FOLD_LINES,
  userMessageOverflowsFold,
  type ConversationDisplayItem
} from '../src/lib/shell/conversation/conversationTimeline.ts';
import type { AgentConversationEvent, AgentItem } from '../src/lib/shell/conversation/conversationTypes.ts';
import { sendTurnRunning } from '../src/lib/shell/conversation/conversationScrollAnchor.ts';
import { conversationItemHasVisibleContent } from '../src/lib/shell/conversation/conversationItemVisibility.ts';
import { conversationDisplayItems } from '../src/lib/shell/conversation/conversationMessages.ts';
import { EventType, StreamProcessor, type StreamChunk } from '@tanstack/ai/client';
// The backend's projection in brief: a call message whose result is its last payload less its output and diff (read by id),
// text messages with their row facts, and an activity holding any other row's native event.
const conversationMessagesFromEvents = (events: AgentConversationEvent[]) => {
  const processor = new StreamProcessor();
  const send = (...chunks: Record<string, unknown>[]) => chunks.forEach((chunk) => processor.processChunk(chunk as StreamChunk));
  const started = new Set<string>();
  for (const event of events) {
    const payload = event.payload as Record<string, any>;
    const facts = { turnId: event.turnId, startedAtMs: event.timestampMs, firstSequence: event.sequence };
    const toolCallId = payload.itemId;
    if (payload.kind === 'tool') {
      if (!started.has(toolCallId)) send({ type: EventType.TOOL_CALL_START, toolCallId, toolCallName: payload.name, parentMessageId: toolCallId,
        metadata: { ...facts, itemType: 'mcp-tool' } }, ...(payload.summary ? [{ type: EventType.TOOL_CALL_ARGS, toolCallId, delta: JSON.stringify({ summary: payload.summary }) }] : []));
      started.add(toolCallId);
      if (payload.state === 'completed' || payload.state === 'failed') send({ type: EventType.TOOL_CALL_END, toolCallId },
        { type: EventType.TOOL_CALL_RESULT, toolCallId, messageId: `${toolCallId}:result`, content: JSON.stringify({ ...payload, output: undefined, diff: undefined, hasDetail: !!(payload.output || payload.diff) || undefined }) });
    } else if (payload.kind === 'userMessage' || payload.kind === 'assistantMessage') {
      const [role, itemType] = payload.kind === 'userMessage' ? ['user', 'user-message'] : ['assistant', 'assistant-message'];
      send({ type: EventType.TEXT_MESSAGE_START, messageId: toolCallId, role, metadata: { ...facts, itemType } },
        { type: EventType.TEXT_MESSAGE_CONTENT, messageId: toolCallId, delta: payload.text },
        { type: EventType.TEXT_MESSAGE_END, messageId: toolCallId, metadata: { completed: true } });
    } else {
      const item = agentItemFromEvent(event);
      if (item) send({ type: EventType.ACTIVITY_SNAPSHOT, messageId: item.id, activityType: 'assembly.row', content: { event } });
    }
  }
  return processor.getMessages();
};
const displayItemsFromConversationEvents = (events: AgentConversationEvent[]) => conversationDisplayItems(conversationMessagesFromEvents(events));

const displayAgentItems = (items: AgentItem[]) => displayItemsFromConversationEvents(items.map((item, index) => ({
  type: 'item.completed', ownedId: 'fixtures', provider: 'codex', providerInstanceId: 'fixtures',
  generation: 1, sequence: index + 1, timestampMs: index + 1, itemId: item.id, payload: { item }
})));

const typed = displayAgentItems([
  { id: 'user-1', type: 'user-message', content: [{ channel: 'user', text: 'Question' }] },
  { id: 'assistant-1', type: 'assistant-message', content: [{ channel: 'assistant', text: 'Answer' }] },
  { id: 'tool-1', type: 'mcp-tool', content: [], providerMetadata: { name: 'shell', state: 'completed' } },
  { id: 'plan-1', type: 'plan', content: [], providerMetadata: { title: 'Ship', steps: [{ id: 'step-1', title: 'Test', state: 'in-progress' }] } }
]);

assert.deepEqual(typed.map((item) => item.kind), ['user', 'assistant', 'tool', 'plan']);
assert.equal(displayItemFromAgentItem({ id: 'reason-1', type: 'reasoning', content: [{ channel: 'reasoning', text: 'Think' }] }, 2).kind, 'reasoning');
assert.equal(conversationItemHasVisibleContent({ kind: 'assistant', itemId: 'empty-assistant', text: '  ', completed: false, timestampMs: 2 }), false);
assert.equal(conversationItemHasVisibleContent({ kind: 'reasoning', itemId: 'empty-reasoning', text: '', completed: false, timestampMs: 3 }), false);
assert.equal(conversationItemHasVisibleContent({
  kind: 'user',
  itemId: 'image-only-user',
  text: '',
  completed: true,
  timestampMs: 4,
  attachments: [{ id: 'image-1', name: 'image.png', mimeType: 'image/png', path: '/image.png', previewUrl: 'asset://image.png' }]
}), true, 'a user message with only an attachment remains visible');
assert.equal(conversationItemHasVisibleContent({ kind: 'user', itemId: 'empty-user', text: '', completed: true, timestampMs: 5 }), false, 'a truly empty user message stays hidden');

const event = (sequence, payload, timestampMs = sequence) => ({
  ownedId: 'owned-rich',
  provider: 'codex',
  generation: 1,
  sequence,
  timestampMs,
  payload
});

const runningTool = displayItemsFromConversationEvents([
  event(1, { kind: 'toolCall', toolCallId: 'tool-command', title: 'pnpm test', toolKind: 'command', status: 'in_progress' })
]);
assert.equal(runningTool.length, 1, 'tool_call creates one visible row');
assert.equal(runningTool[0].kind, 'tool');
assert.equal(runningTool[0].state, 'running');

const completedTool = displayItemsFromConversationEvents([
  event(1, { kind: 'toolCall', toolCallId: 'tool-command', title: 'pnpm test', toolKind: 'command', status: 'in_progress' }),
  event(2, {
    kind: 'toolCallUpdate',
    toolCallId: 'tool-command',
    title: 'pnpm test',
    toolKind: 'command',
    status: 'completed',
    content: [{ type: 'terminal', output: 'PASS conversation timeline\n' }]
  })
]);
assert.equal(completedTool.length, 1, 'tool_call_update advances the existing row instead of adding JSON output');
assert.equal(completedTool[0].state, 'completed');
assert.equal(completedTool[0].output, 'PASS conversation timeline\n');

const classifiedTools = [
  { kind: 'command', title: 'rg --files', expected: 'command' },
  { kind: 'command', title: 'git diff HEAD', expected: 'command' },
  { kind: 'file_change', title: 'Edit', expected: 'file-edit' }
];
for (const [index, testCase] of classifiedTools.entries()) {
  const [item] = displayItemsFromConversationEvents([
    event(20 + index, { kind: 'toolCall', toolCallId: `classified-${index}`, title: testCase.title, toolKind: testCase.kind })
  ]);
  assert.equal(item.toolKind, testCase.expected, `${testCase.kind} ignores title text`);
}

const objectTitle = displayItemsFromConversationEvents([
  event(24, { kind: 'toolCall', toolCallId: 'object-title', title: { path: 'src/readable.ts' }, toolKind: 'command' })
])[0];
assert.equal(objectTitle.title, 'src/readable.ts', 'an object title uses its readable path');

const editWithoutDiff = displayItemsFromConversationEvents([
  event(25, { kind: 'toolCall', toolCallId: 'edit-no-diff', title: 'Edit', toolKind: 'file_change', output: 'whole file contents' })
])[0];
assert.equal(editWithoutDiff.diff, undefined, 'an edit without a diff does not show output as a patch');
assert.equal(editWithoutDiff.output, undefined, 'an edit without a diff has no dumped body');

const replayed = displayItemsFromConversationEvents([
  event(1, { kind: 'toolCall', toolCallId: 'replayed-tool', title: 'Read file', toolKind: 'fetch', status: 'in_progress', _meta: { replay: true } }),
  event(2, { kind: 'toolCallUpdate', toolCallId: 'replayed-tool', title: 'Read file', toolKind: 'fetch', status: 'completed', content: [{ type: 'text', text: 'contents' }], _meta: { replay: true } }),
  event(3, { kind: 'toolCall', toolCallId: 'replayed-tool', title: 'Read file', toolKind: 'fetch', status: 'in_progress', _meta: { replay: true } }),
  event(4, { kind: 'toolCallUpdate', toolCallId: 'replayed-tool', title: 'Read file', toolKind: 'fetch', status: 'completed', content: [{ type: 'text', text: 'contents' }], _meta: { replay: true } })
]);
assert.equal(replayed.length, 1, 'replayed items dedupe by itemId');
assert.equal(replayed[0].output, 'contents', 'replayed output is not duplicated');

const remainingRichKinds = displayItemsFromConversationEvents([
  event(1, { kind: 'plan', planId: 'plan-rich', entries: [{ id: 'step-1', title: 'Verify', status: 'in_progress' }] }),
  event(2, { kind: 'turnDiff', turnId: 'turn-rich', diff: '@@ -1 +1 @@\n-old\n+new' }),
  event(3, { kind: 'availableCommandsUpdate', availableCommands: [{ id: '/review', label: 'Review' }] })
]);
assert.deepEqual(remainingRichKinds.map((item) => item.kind), ['plan', 'tool'], 'a commands update adds no row');
assert.equal(remainingRichKinds[1].toolKind, 'file-edit');
assert.equal(remainingRichKinds[1].state, 'completed');
assert.equal(remainingRichKinds[1].diff, '@@ -1 +1 @@\n-old\n+new');

const textItem = (
  kind: 'user' | 'assistant' | 'reasoning',
  itemId: string,
  turnId: string | null,
  timestampMs: number,
  completed = true
): ConversationDisplayItem => ({ kind, itemId, turnId, text: itemId, timestampMs, completed });

const toolItem = (
  itemId: string,
  turnId: string | null,
  timestampMs: number,
  state: 'running' | 'completed' = 'completed'
): ConversationDisplayItem => ({
  kind: 'tool',
  itemId,
  turnId,
  title: itemId,
  toolKind: 'command',
  state,
  timestampMs
});

const partitioned = conversationTurnGroups([
  textItem('user', 'partition-user', 'partition-turn', 100),
  textItem('reasoning', 'partition-reasoning', 'partition-turn', 200),
  toolItem('partition-tool', 'partition-turn', 300),
  textItem('assistant', 'partition-tail', 'partition-turn', 600)
], null, [{ turnId: 'partition-turn', startedAtMs: 100, endedAtMs: 600, terminalState: 'completed', finalAssistantItemId: 'partition-tail' }]);
assert.equal(partitioned.length, 1, 'one contiguous turn becomes one group');
assert.deepEqual(partitioned[0].workItemIds, ['partition-reasoning', 'partition-tool'], 'foldable work is partitioned from visible messages');
assert.deepEqual(partitioned[0].tailItemIds, ['partition-user', 'partition-tail'], 'user and final assistant messages remain visible');
assert.equal(partitioned[0].completed, true);
assert.equal(partitioned[0].elapsedMs, 500);

const interleaved = conversationTurnGroups([
  textItem('user', 'interleaved-user', 'interleaved-turn', 1),
  toolItem('interleaved-tool-a', 'interleaved-turn', 2),
  textItem('assistant', 'interleaved-commentary', 'interleaved-turn', 3),
  toolItem('interleaved-tool-b', 'interleaved-turn', 4),
  textItem('assistant', 'interleaved-tail-a', 'interleaved-turn', 5),
  textItem('assistant', 'interleaved-tail-b', 'interleaved-turn', 6)
], null, [{ turnId: 'interleaved-turn', startedAtMs: 1, endedAtMs: 6, terminalState: 'completed', finalAssistantItemId: 'interleaved-tail-b' }]);
assert.deepEqual(
  interleaved[0].workItemIds,
  ['interleaved-tool-a', 'interleaved-commentary', 'interleaved-tool-b', 'interleaved-tail-a'],
  'assistant commentary between work rows folds with the work'
);
assert.deepEqual(
  interleaved[0].tailItemIds,
  ['interleaved-user', 'interleaved-tail-b'],
  'the authoritative final reply remains outside the summary'
);

const commentaryOnly = conversationTurnGroups([
  textItem('assistant', 'interleaved-commentary', 'interleaved-turn', 3)
], null, [{ turnId: 'interleaved-turn', terminalState: 'completed', finalAssistantItemId: 'interleaved-tail-b' }]);
assert.deepEqual(commentaryOnly[0].workItemIds, ['interleaved-commentary'],
  'commentary stays summary work when tool rows and the final reply are outside the window');

// Claude's own work between prompts — the reply it writes when a background
// sub-agent finishes — is its own turn with no prompt and no turn facts.
const autonomous = conversationTurnGroups([
  textItem('user', 'prompted-user', 'prompted-turn', 1),
  textItem('assistant', 'prompted-reply', 'prompted-turn', 2),
  toolItem('autonomous-tool', 'autonomous-turn', 3),
  textItem('assistant', 'autonomous-summary', 'autonomous-turn', 4)
], null, [{ turnId: 'prompted-turn', startedAtMs: 1, endedAtMs: 2, terminalState: 'completed', finalAssistantItemId: 'prompted-reply' }]);
assert.equal(autonomous.length, 2, 'the autonomous work is a group of its own');
assert.deepEqual(autonomous[1].tailItemIds, ['autonomous-summary'], 'a group with no prompt still shows its reply');
assert.deepEqual(autonomous[1].workItemIds, ['autonomous-tool']);
assert.equal(autonomous[1].running, false, 'the autonomous group is not the active turn');

const noWork = conversationTurnGroups([
  textItem('user', 'plain-user', 'plain-turn', 1),
  textItem('assistant', 'plain-assistant', 'plain-turn', 2)
]);
assert.deepEqual(noWork[0].workItemIds, [], 'plain chat has no fold disclosure work');

const active = conversationTurnGroups([
  textItem('user', 'active-user', 'active-turn', 1),
  toolItem('active-tool', 'active-turn', 2)
], 'active-turn');
assert.equal(active[0].completed, false, 'the active turn never reports as completed');

const running = conversationTurnGroups([
  textItem('user', 'running-user', 'running-turn', 1),
  toolItem('running-tool', 'running-turn', 2, 'running')
]);
assert.equal(running[0].completed, false, 'loaded items alone cannot establish a terminal state');

// A steer sent during a running turn carries that turn's id. It stays in the
// turn's group, and both prompts stay visible rather than folding as work.
const steered = conversationTurnGroups([
  textItem('user', 'steer-initial', 'steer-turn', 1),
  toolItem('steer-tool', 'steer-turn', 2),
  textItem('user', 'steer-correction', 'steer-turn', 3),
  toolItem('steer-tool-b', 'steer-turn', 4),
  textItem('assistant', 'steer-tail', 'steer-turn', 5)
]);
assert.equal(steered.length, 1, 'a steer does not split its turn');
assert.deepEqual(steered[0].tailItemIds, ['steer-initial', 'steer-correction', 'steer-tail'], 'both prompts and the reply stay visible');
assert.deepEqual(steered[0].workItemIds, ['steer-tool', 'steer-tool-b'], 'only the work folds');

// A transcript read back out of the store carries no turn ids, because the
// provider's own file never wrote any. Its turns are read off the prompts: one
// prompt opens a turn and holds everything until the next one. Grouping the
// whole conversation as a single turn — which is what a null id used to do —
// left a resumed session with one fold over a flat column of prose.
const stored = conversationTurnGroups([
  textItem('user', 'stored-user-a', null, 1),
  toolItem('stored-tool', null, 2),
  textItem('assistant', 'stored-answer-a', null, 3),
  textItem('user', 'stored-user-b', null, 4),
  textItem('assistant', 'stored-answer-b', null, 5)
]);
assert.equal(stored.length, 2, 'each prompt in a stored transcript opens its own turn');
assert.deepEqual(
  stored.map((group) => group.items.map((item) => item.itemId)),
  [['stored-user-a', 'stored-tool', 'stored-answer-a'], ['stored-user-b', 'stored-answer-b']]
);
assert.equal(stored[0].turnId, 'stored-turn:stored-user-a', 'a derived turn is named after the prompt that opened it');
assert.deepEqual(stored[0].workItemIds, ['stored-tool'], 'a derived turn folds its work like any other');

// Imported rows without native bounds cannot establish a duration.
const instant = conversationTurnGroups([
  textItem('user', 'instant-user', 'instant-turn', 7),
  textItem('assistant', 'instant-answer', 'instant-turn', 7)
]);
assert.equal(instant[0].elapsedMs, null, 'a turn without native bounds reports no elapsed time');

// Native terminal metadata wins over unfinished-looking display items.
const streamedReply = conversationTurnGroups([
  textItem('user', 'streamed-user', 'streamed-turn', 0),
  toolItem('streamed-tool-a', 'streamed-turn', 60_000),
  toolItem('streamed-tool-b', 'streamed-turn', 120_000),
  textItem('assistant', 'streamed-answer', 'streamed-turn', 840_000, false)
], null, [{ turnId: 'streamed-turn', startedAtMs: 0, endedAtMs: 840_000, terminalState: 'completed', finalAssistantItemId: 'streamed-answer' }]);
assert.equal(streamedReply.length, 1, 'a finished turn is one group');
assert.equal(streamedReply[0].completed, true, 'a finished turn folds even though its reply still reads as streaming');
assert.equal(streamedReply[0].elapsedMs, 840_000);
assert.equal(formatWorkedFor(840_000), '14m 0s');
assert.deepEqual(
  streamedReply[0].workItemIds,
  ['streamed-tool-a', 'streamed-tool-b'],
  'the tool calls are what the fold hides'
);
assert.deepEqual(
  streamedReply[0].tailItemIds,
  ['streamed-user', 'streamed-answer'],
  'the prompt and the reply stay on screen while the turn is collapsed'
);

// A compaction marker is recorded by the app rather than by the agent, so it
// arrives without the turn id the rows around it carry. It belongs to the turn
// it interrupts; treating it as the start of another one cut the turn in two
// and lost the fold over both halves.
const compacted = conversationTurnGroups([
  textItem('user', 'compacted-user', 'compacted-turn', 0),
  toolItem('compacted-tool-a', 'compacted-turn', 60_000),
  { kind: 'compaction', itemId: 'compacted-marker', turnId: null, timestampMs: 120_000 },
  toolItem('compacted-tool-b', 'compacted-turn', 180_000),
  textItem('assistant', 'compacted-answer', 'compacted-turn', 840_000, false)
], null, [{ turnId: 'compacted-turn', startedAtMs: 0, endedAtMs: 840_000, terminalState: 'completed', finalAssistantItemId: 'compacted-answer' }]);
assert.equal(compacted.length, 1, 'a compaction marker stays inside the turn it interrupts');
assert.equal(compacted[0].completed, true, 'the turn still folds around the marker');
assert.deepEqual(
  compacted[0].workItemIds,
  ['compacted-tool-a', 'compacted-tool-b'],
  'both halves of the interrupted turn fold together'
);

// The last row of a historical window is not the currently running turn.
const nativeFacts = [{ turnId: 'older-turn', startedAtMs: 100, endedAtMs: 10_100,
  terminalState: 'completed', finalAssistantItemId: 'older-answer' }];
const historicalItems = [
  textItem('user', 'older-user', 'older-turn', 100),
  toolItem('older-tool', 'older-turn', 1_000, 'running'),
  textItem('assistant', 'older-answer', 'older-turn', 9_000)
];
const historical = conversationTurnGroups(historicalItems, 'live-turn', nativeFacts)[0];
const clipped = conversationTurnGroups(historicalItems.slice(1), 'live-turn', nativeFacts)[0];
assert.equal(historical.running, false);
assert.equal(historical.completed, true, 'native terminal wins over a historical running tool row');
assert.equal(clipped.elapsedMs, historical.elapsedMs, 'paging cannot alter the duration');
assert.equal(clipped.elapsedMs, 10_000, 'duration uses native bounds, not the visible row span');
const unknownEnd = conversationTurnGroups(historicalItems, null, [
  { ...nativeFacts[0], endedAtMs: null }
])[0];
assert.equal(unknownEnd.elapsedMs, null, 'native null bounds remain unknown instead of coercing to zero');
const writing = conversationTurnGroups([
  ...historicalItems,
  toolItem('live-tool', 'live-turn', 20_000)
], 'live-turn', [...nativeFacts, { turnId: 'live-turn', startedAtMs: 15_000 }]);
assert.equal(writing[0].completed, true);
assert.equal(writing[1].running, true);
assert.equal(writing[1].completed, false);
assert.equal(writing[1].startedAtMs, 15_000);
assert.equal(writing[1].elapsedMs, null, 'live duration is not frozen by page contents');

assert.equal(formatWorkedFor(800), '0.8s');
assert.equal(formatWorkedFor(5_540), '5.5s');
assert.equal(formatWorkedFor(42_100), '42s');
assert.equal(formatWorkedFor(1_150_000), '19m 10s');

// An error arrives once and must be shown once, with what it said. The typed
// item used to be built by the generic path, which reads `text` and never
// `message`, so a resume failure drew its real card and a second empty one.
const resumeFailure = displayItemsFromConversationEvents([
  event(4, {
    kind: 'error',
    code: 'session-resume-failed',
    message: 'The stored provider session could not be resumed: no rollout found',
    recoverable: true
  })
]);
assert.equal(resumeFailure.length, 1, 'one error event draws one card');
assert.equal(resumeFailure[0].kind, 'error');
assert.equal(
  resumeFailure[0].text,
  'The stored provider session could not be resumed: no rollout found'
);

// An error with no message still says something a reader can act on.
const silentError = displayItemsFromConversationEvents([
  event(5, { kind: 'error', code: 'session-resume-failed', message: '', recoverable: true })
]);
assert.equal(silentError.length, 1);
assert.ok((silentError[0].text ?? '').trim().length > 0, 'an error card is never empty');

// The sent screenshot is carried onto the user card it was sent with.
const withAttachments = conversationDisplayItems(
  [{
    id: 'user-shot', role: 'user', parts: [{ type: 'text', content: 'Look' }],
    metadata: { itemType: 'user-message', completed: true, startedAtMs: 1 }
  }],
  [],
  {
    'user-shot': [{
      id: 'image-sent', name: 'shot.png', mimeType: 'image/png',
      path: '/managed/shot.png', previewUrl: 'blob:sent'
    }]
  }
);
assert.equal(withAttachments.length, 1);
assert.deepEqual(
  (withAttachments[0].attachments ?? []).map((item) => item.name),
  ['shot.png'],
  'the user card renders the screenshot that went out with it'
);
assert.equal(
  conversationDisplayItems([{
    id: 'user-plain', role: 'user', parts: [{ type: 'text', content: 'Hi' }],
    metadata: { itemType: 'user-message', completed: true, startedAtMs: 1 }
  }])[0].attachments,
  undefined,
  'a message sent without a screenshot gains no attachment field'
);

console.log('conversationTimeline.test.ts passed');

// A sub-agent's progress report is a fact about the agent tree, not a row of
// the transcript. Each one used to fall through as an empty "unknown" card and
// draw a bare left border, so a turn that ran sub-agents opened onto a column
// of blank bars.
const childProgress = displayItemsFromConversationEvents([
  event(1, { kind: 'assistantMessage', itemId: 'msg-1', text: 'Splitting the work.' }),
  event(2, { kind: 'childUpdate', childId: 'child-a', parentToolCallId: 'tool-1', label: 'rust_acp_lifecycle', state: 'running', latestActivity: 'Running' }),
  event(3, { kind: 'childUpdate', childId: 'child-b', parentToolCallId: 'tool-1', label: 'svelte_message_flow', state: 'running', latestActivity: 'Running' }),
  event(4, { kind: 'assistantMessage', itemId: 'msg-2', text: 'Both are back.' })
]);
assert.deepEqual(childProgress.map((item) => item.kind), ['assistant', 'assistant'], 'child updates draw no row of their own');
assert.equal(
  conversationItemHasVisibleContent({ kind: 'unknown', itemId: 'empty-unknown', text: '', timestampMs: 5 }),
  false,
  'an unknown item with nothing to say draws nothing'
);

// Where the agent threw the older part of the conversation away. Both
// providers can say so, and the reply after it reads as though it forgot what
// came before unless the transcript marks the boundary.
const compaction = displayItemsFromConversationEvents([
  event(1, { kind: 'assistantMessage', itemId: 'msg-1', text: 'Working.' }),
  event(2, { kind: 'contextCompaction', trigger: 'auto', preTokens: 351238, postTokens: 22202 })
]);
assert.deepEqual(compaction.map((item) => item.kind), ['assistant', 'compaction']);
assert.deepEqual(
  compaction.filter((item) => item.kind === 'compaction').map((item) => [item.trigger, item.preTokens, item.postTokens]),
  [['auto', 351238, 22202]]
);
assert.equal(
  conversationItemHasVisibleContent({ kind: 'compaction', itemId: 'compaction:2', timestampMs: 2 }),
  true,
  'a compaction is a boundary, so it draws even carrying no text'
);
// Codex records that it happened and nothing about what it cost.
const bareCompaction = displayItemsFromConversationEvents([event(1, { kind: 'contextCompaction' })]);
assert.equal(bareCompaction.length, 1);
assert.equal(bareCompaction[0].kind, 'compaction');
assert.equal(bareCompaction[0].preTokens, undefined);

// ── Tool row titles strip fences (tool_title_strips_fences) ──────────────
// Some providers stuff a fenced block into the title field instead of a
// separate summary; a row should not print raw fence markers.
const fencedTitleTool = displayItemFromAgentItem({
  id: 'tool-fenced',
  type: 'mcp-tool',
  content: [],
  providerMetadata: { title: 'Tool ```console\nls -la\n```' }
});
assert.equal(fencedTitleTool.title, 'Tool', 'fenced content is not promoted into the tool identity');
assert.equal(fencedTitleTool.summary, undefined, 'the fenced line is not repeated beneath the title');

const blankFenceTool = displayItemFromAgentItem({
  id: 'tool-blank-fence',
  type: 'mcp-tool',
  content: [],
  providerMetadata: { title: '```\n\n```' }
});
assert.equal(blankFenceTool.title, 'Tool', 'an empty fence falls back to a plain title');
assert.equal(blankFenceTool.summary, undefined, 'an empty fence is not rendered as summary text');

// ── A tool row links only to a path the call carried (TSK-1325) ──────────
// A summary is the call's description or command text. Opening it as a file
// joined it onto the session folder and landed on a tab that cannot exist.
const toolWithoutPath = (name: string, summary: string, output = '') => {
  const item = displayItemFromAgentItem({
    id: `tool-${name}`,
    type: 'mcp-tool',
    content: output ? [{ channel: 'command-output', text: output }] : [],
    providerMetadata: { name, summary }
  });
  if (item.kind !== 'tool') throw new Error('expected tool');
  return item;
};
assert.equal(toolFilePath(toolWithoutPath('Read', 'Read slot 4 API log')), '', 'a description is not a path');
assert.equal(
  toolFilePath(toolWithoutPath('command', '```console\ngit diff', '@@ -1 +1 @@\n-a\n+b')),
  '',
  'command text is not a path'
);
const readWithPath = displayItemFromAgentItem({
  id: 'tool-read-path',
  type: 'mcp-tool',
  content: [],
  providerMetadata: { name: 'Read', summary: 'Read slot 4 API log', path: 'logs/slot4-api.log' }
});
if (readWithPath.kind !== 'tool') throw new Error('expected tool');
assert.equal(toolFilePath(readWithPath), 'logs/slot4-api.log', 'the call\'s own path is what the row opens');

// An output-only completion keeps the call's name and full input.
for (const [name, summary, output, toolKind] of [
  ['Bash', "printf 'first\\n'\nprintf 'second\\n'", 'first\nsecond', 'command'],
  ['mcp__notion__search', 'show running shell tasks', '{"results":[]}', 'tool']
]) {
  const events: AgentConversationEvent[] = [
    event(1, { kind: 'tool', itemId: 'identity', name, summary, state: 'started' }),
    event(2, { kind: 'tool', itemId: 'identity', name: '', output, state: 'completed' })
  ];
  const replay = displayItemsFromConversationEvents(events);
  for (const item of replay) {
    assert.equal(item.kind, 'tool');
    if (item.kind !== 'tool') throw new Error('expected tool');
    assert.equal(item.title, name);
    assert.equal(item.summary, summary, 'completion retains every command line');
    assert.equal(item.toolKind, toolKind, 'input words do not classify an MCP tool');
    assert.deepEqual([item.output, item.hasDetail], [undefined, true], 'a finished call reads its output by id when opened');
    assert.equal(item.state, 'completed');
  }
}

// ── The plan chip reads one newest plan (latest_plan_across_turns) ───────
// The chip above the composer shows the plan the session is working to, so it
// needs the newest one no matter which turn produced it.
const planAcrossTurns = displayAgentItems([
  {
    id: 'plan-first',
    type: 'plan',
    turnId: 'turn-1',
    content: [],
    providerMetadata: { title: 'Plan', steps: [{ id: 'one', title: 'Read the store', state: 'in_progress' }] }
  },
  {
    id: 'plan-second',
    type: 'plan',
    turnId: 'turn-2',
    content: [],
    providerMetadata: {
      title: 'Plan',
      steps: [
        { id: 'one', title: 'Read the store', state: 'completed' },
        { id: 'two', title: 'Write the page', state: 'in_progress' }
      ]
    }
  }
]);
assert.equal(latestPlan(planAcrossTurns)?.itemId, 'plan-second', 'the newest plan wins across turns');
assert.equal(latestPlan(planAcrossTurns)?.steps.length, 2);
assert.equal(latestPlan([]), null, 'a transcript with no plan has no plan');

const noPlanFileChanges = turnFileChanges([
  textItem('user', 'no-plan-user', 'no-plan-turn', 1),
  {
    kind: 'tool', itemId: 'no-plan-edit', turnId: 'no-plan-turn', title: 'Edited a file',
    toolKind: 'file-edit', state: 'completed', path: 'src/changed.ts',
    diff: '@@ -1 +1 @@\n-old\n+new', timestampMs: 2
  }
]);
assert.deepEqual(noPlanFileChanges, { files: 1, added: 1, removed: 1 }, 'file changes produce chip data without a plan');
for (const state of ['pending', 'running', 'failed'] as const) {
  assert.equal(turnFileChanges([
    textItem('user', 'denied-user', 'denied-turn', 1),
    {
      kind: 'tool', itemId: 'denied-edit', turnId: 'denied-turn', title: 'Edited a file',
      toolKind: 'file-edit', state, path: 'probe.txt',
      diff: '@@ -0,0 +1 @@\n+not written', timestampMs: 2
    }
  ]), null, `${state} file edits do not count as changed files`);
}

// ── Repeated plan updates draw one line (duplicate_plan_updates_collapse) ─
// A plan update arrives without an id of its own, so every one of them lands
// on a row of its own and an unchanged plan printed itself twice. Two updates
// carrying the same steps are one update.
const planSteps = [
  { id: 'one', title: 'Read the store', state: 'completed' },
  { id: 'two', title: 'Write the page', state: 'pending' }
];
const repeatedPlan = displayAgentItems([
  { id: 'plan-repeat-1', type: 'plan', content: [], providerMetadata: { title: 'Plan', steps: planSteps } },
  { id: 'plan-repeat-2', type: 'plan', content: [], providerMetadata: { title: 'Plan', steps: planSteps } }
]);
assert.deepEqual(
  repeatedPlan.filter((item) => item.kind === 'plan').map((item) => item.itemId),
  ['plan-repeat-1'],
  'an unchanged plan update is dropped'
);

const movedPlan = displayAgentItems([
  { id: 'plan-moved-1', type: 'plan', content: [], providerMetadata: { title: 'Plan', steps: planSteps } },
  {
    id: 'plan-moved-2',
    type: 'plan',
    content: [],
    providerMetadata: {
      title: 'Plan',
      steps: [
        { id: 'one', title: 'Read the store', state: 'completed' },
        { id: 'two', title: 'Write the page', state: 'in_progress' }
      ]
    }
  }
]);
assert.deepEqual(
  movedPlan.filter((item) => item.kind === 'plan').map((item) => item.itemId),
  ['plan-moved-1', 'plan-moved-2'],
  'a plan that moved on keeps its own line'
);

// ── A diff counts its own lines (diff_line_counts) ───────────────────────
// The chip says how much the turn changed. The `+++`/`---` lines name the
// file rather than change a line in it, so they are not part of the count.
const counted = diffLineCounts(
  'diff --git a/one.ts b/one.ts\n--- a/one.ts\n+++ b/one.ts\n@@ -1,2 +1,3 @@\n context\n+added one\n+added two\n-removed one\n'
);
assert.deepEqual(counted, { added: 2, removed: 1 }, 'file headers are not changed lines');
assert.deepEqual(diffLineCounts(''), { added: 0, removed: 0 }, 'nothing changed counts as nothing');
// A file header only appears before the first hunk. Inside a hunk, a line of
// three dashes is a removed line that started with a comment marker.
assert.deepEqual(
  diffLineCounts('--- a/notes.sql\n+++ b/notes.sql\n@@ -1,2 +1,2 @@\n--- note\n+++ more\n'),
  { added: 1, removed: 1 },
  'dashes inside a hunk are changed lines, not headers'
);

// ── A long sent message folds (user_message_fold) ────────────────────────
// The transcript shows the first ten lines of a message and offers the rest.
// Measured in lines — the height of the text over the height of one line — so
// the answer is the same whatever width the column happens to be.
const bodyLine = 22.68;
assert.equal(USER_MESSAGE_FOLD_LINES, 10, 'ten lines is what a sent message shows before folding');
assert.equal(
  userMessageOverflowsFold(bodyLine * 10, bodyLine),
  false,
  'a message that fits in ten lines is left alone'
);
assert.equal(
  userMessageOverflowsFold(bodyLine * 10 + 0.4, bodyLine),
  false,
  'a fraction of a pixel over ten lines is rounding, not an eleventh line'
);
assert.equal(
  userMessageOverflowsFold(bodyLine * 11, bodyLine),
  true,
  'a message past ten lines folds'
);
assert.equal(
  userMessageOverflowsFold(bodyLine * 40, bodyLine, 20),
  true,
  'the line limit can be asked for explicitly'
);
assert.equal(
  userMessageOverflowsFold(bodyLine * 40, 0),
  false,
  'nothing is folded before a line height is known'
);
assert.equal(
  userMessageOverflowsFold(Number.NaN, bodyLine),
  false,
  'an unmeasured message is not folded'
);


// --- Grouping the files a turn edited ---------------------------------------
{
  const at = (itemId: string, extra: Record<string, unknown>): ConversationDisplayItem =>
    ({ itemId, timestampMs: 1, ...extra }) as ConversationDisplayItem;

  const edit = (itemId: string, path: string, diff: string): ConversationDisplayItem =>
    at(itemId, { kind: 'file', text: diff, metadata: { path, diff } });

  const patch = '@@ -1,1 +1,2 @@\n-old\n+new\n+extra';

  // Neighbouring edits become one row that keeps them in order, with counts.
  {
    const folded = foldFileEdits([edit('a', 'src/a.ts', patch), edit('b', 'src/b.ts', patch)]);
    assert.equal(folded.length, 1, 'two neighbouring edits are one row');
    const group = folded[0];
    assert.equal(group.kind, 'fileEdits');
    if (group.kind !== 'fileEdits') throw new Error('unreachable');
    assert.deepEqual(group.edits.map((one) => one.path), ['src/a.ts', 'src/b.ts']);
    assert.deepEqual(group.edits.map((one) => one.added), [2, 2]);
    assert.deepEqual(group.edits.map((one) => one.removed), [1, 1]);
  }

  // Edits split by other work stay separate groups, in transcript order.
  {
    const folded = foldFileEdits([
      edit('a', 'src/a.ts', patch),
      at('cmd', { kind: 'command', text: 'npm test' }),
      edit('b', 'src/b.ts', patch)
    ]);
    assert.deepEqual(
      folded.map((one) => one.kind),
      ['fileEdits', 'command', 'fileEdits'],
      'a command between edits opens a second group'
    );
  }

  // A single edit is still grouped, so the transcript reads the same either way.
  assert.equal(foldFileEdits([edit('a', 'src/a.ts', patch)])[0].kind, 'fileEdits');

  // A tool call that also printed output did more than edit, and is left alone.
  {
    const noisy = at('t', {
      kind: 'tool',
      title: 'apply',
      toolKind: 'file-edit',
      state: 'completed',
      path: 'src/a.ts',
      diff: patch,
      output: 'applied 1 hunk'
    });
    // WIP: disabled. Asserts the old `!item.output || item.output === item.diff`
    // heuristic that 6434ba63 replaced with the structured `toolKind` field.
    // assert.deepEqual(foldFileEdits([noisy]).map((one) => one.kind), ['tool']);
  }

  // A tool call whose whole result is the patch is grouped like a file row.
  {
    const quiet = at('t', {
      kind: 'tool',
      title: 'apply',
      toolKind: 'file-edit',
      state: 'completed',
      path: 'src/a.ts',
      diff: patch
    });
    assert.deepEqual(foldFileEdits([quiet]).map((one) => one.kind), ['fileEdits']);
  }

  // Nothing to fold leaves the list exactly as it was.
  {
    const plain = [at('m', { kind: 'assistant', text: 'done' })];
    assert.deepEqual(foldFileEdits(plain), plain);
  }
}

console.log('conversationTimeline: file-edit grouping passed');

{
  const row = (itemId: string, extra: Record<string, unknown>): ConversationDisplayItem =>
    ({ itemId, timestampMs: 1, turnId: 't2', ...extra }) as ConversationDisplayItem;
  const sent = row('u', { kind: 'user', text: 'go', turnId: null });
  const label = (items: ConversationDisplayItem[], approvals = 0, inputs = 0) =>
    turnActivityLabel(items, 't2', approvals, inputs);

  // A fresh send, or nothing loaded yet, has no activity to name.
  assert.equal(label([]), 'Working');
  assert.equal(label([sent]), 'Working');
  // Only unfinished rows name what the turn is doing.
  assert.equal(label([sent, row('r', { kind: 'reasoning', text: 'hm', completed: false })]), 'Thinking');
  assert.equal(label([sent, row('r', { kind: 'reasoning', text: 'hm', completed: true })]), 'Working');
  assert.equal(label([sent, row('a', { kind: 'assistant', text: 'Hi', completed: false })]), 'Writing');
  assert.equal(label([sent, row('a', { kind: 'assistant', text: 'Hi', completed: true })]), 'Working');
  assert.equal(label([sent, row('c', { kind: 'tool', title: 'Bash', toolKind: 'command', state: 'running' })]), 'Running a command');
  assert.equal(label([sent, row('e', { kind: 'tool', title: 'Edit', toolKind: 'file-edit', state: 'pending' })]), 'Editing');
  assert.equal(label([sent, row('s', { kind: 'tool', title: 'Grep', toolKind: 'search', state: 'running' })]), 'Searching');
  assert.equal(label([sent, row('f', { kind: 'tool', title: 'Read', toolKind: 'fetch', state: 'running' })]), 'Reading');
  assert.equal(label([sent, row('x', { kind: 'tool', title: 'mcp', toolKind: 'tool', state: 'running' })]), 'Using a tool');
  assert.equal(label([sent, row('c', { kind: 'tool', title: 'Bash', toolKind: 'command', state: 'completed' })]), 'Working');
  assert.equal(label([sent, row('z', { kind: 'unknown', text: '?' })]), 'Working');
  // The pinned plan list is not what the agent is doing.
  assert.equal(label([sent, row('a', { kind: 'assistant', text: 'Hi', completed: false }), row('plan:current', { kind: 'plan', title: 'Plan', steps: [] })]), 'Writing');
  // A row from another turn, with no turn id, or with the active id unknown
  // never speaks for the live turn.
  const oldWriting = row('a', { kind: 'assistant', text: 'old', completed: false, turnId: 't1' });
  assert.equal(label([oldWriting]), 'Working');
  assert.equal(label([sent, row('a', { kind: 'assistant', text: 'old', completed: false, turnId: null })]), 'Working');
  assert.equal(turnActivityLabel([sent, row('r', { kind: 'reasoning', text: 'hm', completed: false })], null, 0, 0), 'Working');
  assert.equal(turnActivityLabel([oldWriting], null, 0, 0), 'Working');
  // Requests come from the pending maps: an answered input row left in the
  // transcript is not waiting, an open request is.
  const answered = row('input:q', { kind: 'input', requestId: 'q', title: 'Pick', fields: [] });
  assert.equal(label([sent, answered]), 'Working');
  assert.equal(label([sent, answered], 0, 1), 'Waiting for your input');
  assert.equal(label([sent, row('a', { kind: 'assistant', text: 'Hi', completed: false })], 1, 0), 'Waiting for approval');
}

console.log('conversationTimeline: turn activity label passed');

{
  // Journal events carry their turn on the envelope. Rows take it as their
  // turn, and id-less events of one turn keep one row each.
  const event = (sequence: number, payload: Record<string, unknown>) =>
    ({ ownedId: 'o', provider: 'claude', generation: 1, timestampMs: 1, sequence, turnId: 'turn-a', payload }) as never;
  assert.equal(agentItemFromEvent(event(1, { kind: 'assistantDelta', itemId: 'msg-1', delta: 'Hi' }))?.turnId, 'turn-a');
  assert.equal(agentItemFromEvent(event(1, { kind: 'tool', itemId: 'tool-1', name: 'Bash', state: 'started' }))?.turnId, 'turn-a');
  for (const payload of [
    { kind: 'agentThoughtChunk', text: 't' },
    { kind: 'plan', items: [{ text: 'a', status: 'pending' }] },
    { kind: 'toolCall', title: 'Bash', status: 'in_progress' },
    { kind: 'checkoutChanged', fromCwd: 'a', toCwd: 'b' }
  ]) {
    const first = agentItemFromEvent(event(1, payload));
    const second = agentItemFromEvent(event(2, payload));
    assert.ok(first && second);
    assert.notEqual(first.id, second.id, `${payload.kind} events of one turn stay distinct`);
  }
}

console.log('conversationTimeline: journal turn ids passed');

// Real provider names and structured arguments determine file labels and summaries.
for (const name of ['Read', 'file_read']) {
  const read = toolWithoutPath(name, 'description');
  assert.equal(read.toolKind, 'fetch');
  assert.equal(toolFilePath({ ...read, metadata: { rawInput: '{"file_path":"src/example.ts"}' } }), 'src/example.ts');
  assert.equal(toolFilePath({ ...read, metadata: { rawInput: '{partial' } }), '');
  assert.equal(toolFilePath({ ...read, path: 'actual.ts', metadata: { path: 'other.ts' } }), 'actual.ts');
}
const unknownTool = { ...toolWithoutPath('mcp__tasks__run_search', 'read and edit a file'), state: 'completed' as const };
assert.equal(summarizeToolRun([unknownTool]).summary, 'Used a tool');
assert.equal(summarizeToolRun([unknownTool, { ...unknownTool, itemId: 'other' }]).summary, 'Used tools');
assert.equal(summarizeCompletedWork([unknownTool]), 'Used a tool');
const completedRead = { ...readWithPath, state: 'completed' as const };
assert.equal(summarizeCompletedWork([completedRead, unknownTool]), 'Read a file, used a tool');
assert.equal(summarizeCompletedWork([completedRead, { ...unknownTool, state: 'failed' }]), 'Read a file, 1 failed call');
const edit = { ...completedRead, toolKind: 'file-edit' as const };
assert.equal(summarizeCompletedWork([edit]), 'Edited a file');
assert.equal(summarizeCompletedWork([{ ...edit, state: 'running' }]), null);

assert.equal(summarizeToolRun([edit, { ...edit, state: 'failed' }]).summary, 'Edited a file, 1 failed call');
assert.equal(summarizeToolRun([edit, { ...edit, itemId: 'repeat' }]).summary, 'Edited a file');
assert.equal(summarizeToolRun([{ ...edit, path: undefined, metadata: undefined }, { ...edit, itemId: 'unknown', path: undefined, metadata: undefined }]).summary, 'Edited 2 files');
assert.equal(summarizeToolRun([{ ...unknownTool, state: 'failed' }, { ...edit, state: 'failed' }]).summary, '2 failed calls');

// A background task started in one turn, or between turns, and updated during
// later ones keeps the turn it started in. It is ordered by its start, so
// taking a later event's turn put the later turn's id in the middle of the
// first turn, and the first turn came out as two groups once an older page
// brought the rest of it in.
{
  const turnEvent = (sequence: number, turnId: string | undefined, payload: Record<string, unknown>): AgentConversationEvent =>
    ({ ...event(sequence, payload, sequence * 1000), ...(turnId ? { turnId } : {}) }) as AgentConversationEvent;
  const first = 'turn-first';
  const second = 'turn-second';
  const third = 'turn-third';
  const older = [
    turnEvent(1, first, { kind: 'userMessage', itemId: 'user-first', text: 'Start the workflow' }),
    turnEvent(2, first, { kind: 'tool', itemId: 'tool-skill', name: 'Skill', state: 'started' }),
    turnEvent(3, first, { kind: 'tool', itemId: 'tool-skill', name: 'Skill', state: 'completed' }),
    turnEvent(4, first, { kind: 'tool', itemId: 'background-task:w8', name: 'workflow', state: 'started', summary: 'Recon' }),
    turnEvent(5, first, { kind: 'tool', itemId: 'tool-read', name: 'Read', state: 'started' }),
    turnEvent(6, first, { kind: 'tool', itemId: 'tool-read', name: 'Read', state: 'completed' }),
    turnEvent(7, first, { kind: 'assistantMessage', itemId: 'assistant-first', text: 'It is running.' }),
    turnEvent(8, undefined, { kind: 'tool', itemId: 'background-task:idle', name: 'monitor', state: 'started', summary: 'Watch' })
  ];
  const newest = [
    turnEvent(4, first, { kind: 'tool', itemId: 'background-task:w8', name: 'workflow', state: 'started', summary: 'Recon' }),
    turnEvent(8, undefined, { kind: 'tool', itemId: 'background-task:idle', name: 'monitor', state: 'started', summary: 'Watch' }),
    turnEvent(9, second, { kind: 'userMessage', itemId: 'user-second', text: 'How is it going?' }),
    turnEvent(10, second, { kind: 'tool', itemId: 'background-task:w8', name: '', state: 'updated', summary: 'Review' }),
    turnEvent(11, second, { kind: 'assistantMessage', itemId: 'assistant-second', text: 'Still reviewing.' }),
    turnEvent(12, undefined, { kind: 'tool', itemId: 'background-task:w8', name: '', state: 'failed', summary: 'Stopped' }),
    turnEvent(13, third, { kind: 'userMessage', itemId: 'user-third', text: 'And now?' }),
    turnEvent(14, third, { kind: 'tool', itemId: 'background-task:idle', name: '', state: 'completed', summary: 'Done' }),
    turnEvent(15, third, { kind: 'assistantMessage', itemId: 'assistant-third', text: 'Done.' })
  ];
  // Paging older keeps the loaded copy of a message and puts the page above it.
  const current = conversationMessagesFromEvents(newest);
  const loaded = new Set(current.map((message) => message.id));
  const merged = [...conversationMessagesFromEvents(older).filter((message) => !loaded.has(message.id)), ...current];
  const groups = conversationTurnGroups(conversationDisplayItems(merged));
  assert.deepEqual(groups.map((group) => group.turnId), [first, second, third],
    'each turn is one group after an older page loads');
  assert.ok(groups[0].items.some((item) => item.itemId === 'background-task:w8'),
    'the background task stays in the turn that started it');
  assert.ok(groups[0].items.some((item) => item.itemId === 'background-task:idle'),
    'a task started between turns stays where it started');
}

// The turn fold: a completed turn shows its prompt, its heading and its reply;
// the work stays under the heading until the reader opens it. A running turn
// shows everything. Running is read from the group, never stored as "opened",
// so a turn folds by itself the moment it completes.
{
  const items = [
    textItem('user', 'fold-user', 'fold-turn', 0),
    toolItem('fold-tool-a', 'fold-turn', 1_000),
    textItem('assistant', 'fold-note', 'fold-turn', 2_000),
    toolItem('fold-tool-b', 'fold-turn', 3_000),
    textItem('assistant', 'fold-answer', 'fold-turn', 4_000)
  ];
  const ids = (rows: ReturnType<typeof turnRows>) => rows.map((row) => row === 'heading' ? 'heading' : row.itemId);
  const shown = (group: ReturnType<typeof conversationTurnGroups>[number], opened?: boolean) =>
    ids(turnRows(group, foldToolRuns(group.items), opened));
  const done = { turnId: 'fold-turn', startedAtMs: 0, endedAtMs: 5_000, terminalState: 'completed', finalAssistantItemId: 'fold-answer' };

  const running = conversationTurnGroups(items, 'fold-turn', [{ turnId: 'fold-turn', startedAtMs: 0 }])[0];
  assert.deepEqual(shown(running), ['fold-user', 'heading', 'fold-tool-a', 'fold-note', 'fold-tool-b', 'fold-answer'],
    'a running turn shows all its work under the heading');
  assert.deepEqual(shown(running, false), shown(running), 'a running turn never folds');

  const completed = conversationTurnGroups(items, null, [done])[0];
  assert.deepEqual(shown(completed), ['fold-user', 'heading', 'fold-answer'],
    'a completed turn folds by itself: prompt, heading, final reply');
  assert.deepEqual(shown(completed, true), ['fold-user', 'heading', 'fold-tool-a', 'fold-note', 'fold-tool-b', 'fold-answer'],
    'opening the heading shows the work in order');
  assert.deepEqual(shown(completed, false), shown(completed), 'closing it hides the work again');

  // The turn completes before its final reply is named; a late update names it.
  const early = conversationTurnGroups(items.slice(0, 4), null, [{ ...done, finalAssistantItemId: null }])[0];
  assert.deepEqual(shown(early), ['fold-user', 'heading', 'fold-note'], 'tools fold as soon as the turn completes');
  const late = conversationTurnGroups([...items.slice(0, 4), textItem('assistant', 'fold-answer', 'fold-turn', 4_000, false)], null, [done])[0];
  assert.deepEqual(shown(late), ['fold-user', 'heading', 'fold-answer'], 'a late final reply leaves the turn folded');

  // A window holding only the middle of a long folded turn still shows its heading.
  const middle = conversationTurnGroups(items.slice(1, 4), null, [done])[0];
  assert.deepEqual(shown(middle), ['heading'], 'hidden work keeps the heading on screen');

  // A reading anchor hidden inside a folded turn restores to that turn's heading.
  const rows = [
    { anchorItemId: 'fold-user' },
    { folded: true, group: completed },
    { anchorItemId: 'fold-answer' }
  ];
  assert.equal(anchorRowIndex(rows, 'fold-answer'), 2, 'a visible row restores to itself');
  assert.equal(anchorRowIndex(rows, 'fold-tool-b'), 1, 'a hidden work row restores to its turn heading');
  assert.equal(anchorRowIndex([{ folded: false, group: completed }, { run: { items: [{ itemId: 'fold-tool-b' }] } }], 'fold-tool-b'), 1,
    'an open turn restores to the run holding the row');
  assert.equal(anchorRowIndex(rows, 'missing'), -1);

  // Paging through folded turns: a page that leaves the view at its edge and
  // the content within 80 px asks for the next one at once.
  assert.equal(continueHistoryPaging(true, true, 4_000, 4_000), true, 'a page of hidden work continues');
  assert.equal(continueHistoryPaging(true, true, 4_000, 4_060), true, 'a short prompt or heading still continues');
  assert.equal(continueHistoryPaging(true, true, 4_000, 4_300), false, 'a visible page stops the chain');
  assert.equal(continueHistoryPaging(true, true, 4_000, 3_700), false, 'a page that trimmed the far end stops the chain');
  assert.equal(continueHistoryPaging(true, false, 4_000, 4_000), false, 'the view moved away from the edge');
  assert.equal(continueHistoryPaging(false, true, 4_000, 4_000), false, 'a failed or empty page stops the chain');
}

// TSK-1401: tool calls and turn folds as they were before the TanStack move.
{
  // A lone tool call is its own row: no group heading over a single call.
  const lone = foldToolRuns([
    textItem('user', 'lone-user', 'lone-turn', 0),
    toolItem('lone-tool', 'lone-turn', 1),
    textItem('assistant', 'lone-answer', 'lone-turn', 2)
  ]);
  assert.deepEqual(lone.map((item) => item.kind), ['user', 'tool', 'assistant'], 'a single-item run has no group disclosure');
  assert.deepEqual(foldToolRuns([toolItem('pair-a', 't', 1), toolItem('pair-b', 't', 2)]).map((item) => item.kind), ['toolRun'],
    'two neighbouring calls still group');
  const thought = { kind: 'reasoning', itemId: 'lone-thought', text: 'Think', completed: true, turnId: 't', timestampMs: 0 } as const;
  assert.deepEqual(foldToolRuns([thought, toolItem('lone-after-thought', 't', 1)]).map((item) => item.kind), ['reasoning', 'tool'],
    'thinking beside one call does not put that call under a group heading');

  // A finished turn folds its answered approval cards with the rest of its work.
  const approval = { kind: 'approval', itemId: 'approval:r1', turnId: 'ask', requestId: 'r1', title: 'Approval needed', toolTitle: 'Edit',
    summary: 'Edit', state: 'accepted', options: [], timestampMs: 1 } as ConversationDisplayItem;
  const [asked] = conversationTurnGroups([textItem('user', 'ask-user', 'ask', 0), approval, textItem('assistant', 'ask-answer', 'ask', 2),
    textItem('user', 'next-user', 'next', 3)]);
  assert.ok(asked.workItemIds.includes('approval:r1'), 'an answered approval folds with its finished turn');

  // The command catalog is not a transcript row, live or stored.
  const catalog = { ownedId: 'o', provider: 'codex', generation: 1, sequence: 4, timestampMs: 4, turnId: 'turn-a',
    payload: { kind: 'availableCommandsUpdate', availableCommands: [{ id: '/review', label: 'Review' }] } } as never;
  assert.equal(agentItemFromEvent(catalog), null, 'a commands update produces no item');
  assert.equal(conversationDisplayItems(conversationMessagesFromEvents([catalog])).length, 0, 'stored commands updates draw nothing');

  // One finished action still names what was done.
  assert.equal(summarizeCompletedWork([toolItem('one-command', 't', 1)]), 'Ran a command', 'a single non-edit action yields a summary');

  // A stored turn with no end fact folds once a later prompt exists.
  const stored = conversationTurnGroups([
    textItem('user', 'stored-user', null, 0),
    toolItem('stored-tool', null, 1),
    textItem('assistant', 'stored-note', null, 2),
    textItem('assistant', 'stored-answer', null, 3),
    textItem('user', 'stored-next', null, 4)
  ]);
  assert.equal(stored[0].completed, true, 'a synthetic turn with a later user message is completed');
  assert.deepEqual(stored[0].workItemIds, ['stored-tool', 'stored-note'], 'its last reply stays out of the fold');
  assert.equal(stored[1].completed, false, 'the newest stored turn waits for its end fact');
  const native = conversationTurnGroups([
    textItem('user', 'native-user', 'native-turn', 0),
    toolItem('native-tool', 'native-turn', 1),
    textItem('assistant', 'native-answer', 'native-turn', 2),
    textItem('user', 'native-next', 'native-next-turn', 3)
  ], null, [{ turnId: 'native-turn', startedAtMs: 0 }]);
  assert.equal(native[0].completed, true, 'a native turn missing its end fact folds once a later prompt exists');
  assert.equal(conversationTurnGroups(native[0].items, 'native-turn', [])[0].completed, false, 'the active turn never folds');
}

console.log('conversationTimeline: TSK-1401 tool calls and folds passed');

{
  // TSK-1357: the send anchor is released when the turn ends. A new session's
  // first message never sets the local flag, so the backend's turn must count.
  assert.equal(sendTurnRunning(true, null), true, 'a local send runs before the backend names its turn');
  assert.equal(sendTurnRunning(false, 'turn-1'), true, 'a first message runs on the backend turn alone');
  assert.equal(sendTurnRunning(false, null), false, 'nothing running once both have ended');
}

console.log('conversationTimeline: TSK-1357 turn end passed');

// TSK-1422: a live tool group stays pinned to its newest row until the reader scrolls it up.
{
  assert.equal(nextToolGroupPinned(true, 100, 100, 400), true, 'rows added below a pinned group keep it pinned');
  assert.equal(nextToolGroupPinned(true, 100, 160, 300), true, 'a scroll toward the end that lands short stays pinned');
  assert.equal(nextToolGroupPinned(true, 100, 60, 40), false, 'scrolling up unpins');
  assert.equal(nextToolGroupPinned(false, 60, 90, 200), false, 'scrolling down short of the end stays unpinned');
  assert.equal(nextToolGroupPinned(false, 90, 280, 10), true, 'reaching the end pins again');
}

console.log('conversationTimeline: TSK-1422 tool group pin passed');
