import assert from 'node:assert/strict';

import type {
  AgentConversationEvent,
  ConversationMetadata
} from '../src/lib/shell/conversation/conversationTypes.ts';
import {
  sessionContextFacts,
  sessionContextUsage,
  sessionFilesTouched,
  sessionAttachments
} from '../src/lib/shell/panels/context/sessionContextModel.ts';
import type { ConversationAttachment } from '../src/lib/shell/conversation/conversationTypes.ts';

const metadata = (overrides: Partial<ConversationMetadata> = {}): ConversationMetadata => ({
  model: null,
  effort: null,
  approvalPolicy: null,
  usedTokens: null,
  contextWindow: null,
  ...overrides
});

// Nothing selected: three facts, none of them claiming to know anything.
const noSession = sessionContextFacts(null);
assert.equal(noSession.length, 3, 'the panel always shows the same three facts');
assert.deepEqual(
  noSession.map((fact) => fact.missing),
  [true, true, true],
  'with no session every fact is missing'
);
assert.deepEqual(
  noSession.map((fact) => fact.label),
  ['Model', 'Effort', 'Access mode'],
  'the three facts are model, effort and access mode'
);

// A partial report stays partial: what was reported shows, what was not says so.
const partial = sessionContextFacts(metadata({ model: 'sample-model' }));
assert.equal(partial[0].value, 'sample-model');
assert.equal(partial[0].missing, false, 'a reported model is not missing');
assert.equal(partial[1].missing, true, 'an unreported effort stays missing');
assert.equal(partial[2].missing, true, 'an unreported access mode stays missing');

// A percentage needs both halves of the pair. Half a pair yields nothing.
const usedOnly = sessionContextUsage(metadata({ usedTokens: 12_000 }), null);
assert.equal(usedOnly.usedTokens, 12_000);
assert.equal(usedOnly.contextWindow, null);
assert.equal(usedOnly.percentUsed, null, 'a used count alone cannot make a percentage');

const windowOnly = sessionContextUsage(metadata({ contextWindow: 200_000 }), null);
assert.equal(windowOnly.usedTokens, null);
assert.equal(windowOnly.contextWindow, 200_000);
assert.equal(windowOnly.percentUsed, null, 'a window alone cannot make a percentage');

const bothReported = sessionContextUsage(metadata({ usedTokens: 50_000, contextWindow: 200_000 }), {
  inputTokens: 40_000,
  outputTokens: 10_000,
  totalTokens: 950_000
});
assert.equal(bothReported.percentUsed, 25, 'both numbers real gives a real percentage');
assert.equal(bothReported.inputTokens, 40_000);
assert.equal(bothReported.outputTokens, 10_000);
assert.equal(bothReported.totalTokens, 950_000);

const legacyCumulative = sessionContextUsage(null, {
  usedTokens: 2_091_674,
  contextWindow: 237_500,
  inputTokens: 81_896,
  outputTokens: 418
});
assert.equal(legacyCumulative.usedTokens, 82_314);
assert.equal(legacyCumulative.totalTokens, 2_091_674);
assert.equal(legacyCumulative.percentUsed, 35);

const emptyUsage = sessionContextUsage(null, null);
assert.deepEqual(emptyUsage, {
  usedTokens: null,
  contextWindow: null,
  percentUsed: null,
  inputTokens: null,
  outputTokens: null,
  totalTokens: null
});

// Files touched, derived from whatever the provider happened to put on its tool calls.
const toolEvent = (
  sequence: number,
  timestampMs: number,
  payload: Record<string, unknown>
): AgentConversationEvent =>
  ({
    ownedId: 'session-one',
    generation: 1,
    sequence,
    timestampMs,
    payload
  }) as unknown as AgentConversationEvent;

const touched = sessionFilesTouched([
  toolEvent(1, 1_000, { kind: 'toolCall', toolCallId: 'a', path: 'src/one.ts' }),
  toolEvent(2, 2_000, { kind: 'toolCallUpdate', toolCallId: 'a', path: 'src/one.ts' }),
  toolEvent(3, 3_000, { kind: 'toolCall', toolCallId: 'b', path: 'src/one.ts' }),
  toolEvent(4, 4_000, { kind: 'toolCall', toolCallId: 'c' }),
  toolEvent(5, 5_000, { kind: 'agentThoughtChunk', text: 'src/never.ts' }),
  toolEvent(6, 6_000, {
    kind: 'toolCall',
    toolCallId: 'd',
    locations: [{ path: 'src/two.ts', line: 12 }]
  })
], '/workspace');

assert.equal(touched.length, 2, 'only the two real paths survive');
assert.equal(touched[0].path, '/workspace/src/two.ts', 'the most recently touched path resolves against the workspace');
assert.equal(touched[0].count, 1);
assert.equal(touched[0].lastTouchedMs, 6_000);
assert.equal(touched[1].path, '/workspace/src/one.ts');
assert.equal(touched[1].count, 3, 'three references to one path collapse into one entry');
assert.equal(touched[1].lastTouchedMs, 3_000, 'the entry keeps its most recent timestamp');

const normalized = sessionFilesTouched([
  toolEvent(7, 7_000, { kind: 'tool', itemId: 'legacy', name: 'read', state: 'completed', path: 'src/normalized.ts' }),
  toolEvent(8, 8_000, { kind: 'turnDiff', turnId: 'turn', path: 'src/changed.ts', diff: '+change' }),
  toolEvent(9, 9_000, { kind: 'tool', itemId: 'command', name: '/bin/zsh -lc pwd', state: 'completed', path: '/workspace' }),
  toolEvent(10, 10_000, {
    kind: 'tool',
    itemId: 'patch',
    name: 'Apply file changes',
    state: 'completed',
    path: '/workspace/src/first.ts\n/workspace/src/second.ts'
  })
], '/workspace');
assert.deepEqual(
  normalized.map((touch) => touch.path),
  ['/workspace/src/first.ts', '/workspace/src/second.ts', '/workspace/src/changed.ts', '/workspace/src/normalized.ts'],
  'workspace directories are excluded and multi-file tool paths become separate absolute files'
);

assert.deepEqual(sessionFilesTouched([]), [], 'no events means no files');

// A session that has touched hundreds of files still shows only the most recent 50.
const manyEvents = Array.from({ length: 60 }, (_, index) =>
  toolEvent(index, index * 1_000, { kind: 'toolCall', toolCallId: `t${index}`, path: `src/file-${index}.ts` })
);
const capped = sessionFilesTouched(manyEvents);
assert.equal(capped.length, 50, 'the list stops at 50 files even when more were touched');
assert.equal(capped[0].path, 'src/file-59.ts', 'the cap keeps the most recently touched files');
assert.equal(capped[49].path, 'src/file-10.ts', 'the cap drops the oldest touches first');

const newestPage = sessionFilesTouched([
  toolEvent(4, 4_000, { kind: 'tool', path: 'src/edited.ts' }),
  toolEvent(3, 3_000, { kind: 'tool', path: 'src/read.ts' })
], '/workspace');
const olderPage = sessionFilesTouched([
  toolEvent(2, 2_000, { kind: 'tool', path: 'src/edited.ts' }),
  toolEvent(1, 1_000, { kind: 'tool', path: 'src/older.ts' })
], '/workspace');
const pageTouches = new Map(newestPage.map((touch) => [touch.path, touch]));
for (const touch of olderPage) {
  const existing = pageTouches.get(touch.path);
  if (existing) existing.count += touch.count;
  else pageTouches.set(touch.path, touch);
}
assert.deepEqual(
  [...pageTouches.values()].sort((a, b) => b.lastTouchedMs - a.lastTouchedMs).map(({ path, count }) => [path, count]),
  [['/workspace/src/edited.ts', 2], ['/workspace/src/read.ts', 1], ['/workspace/src/older.ts', 1]],
  'older pages add missing files without moving a repeated file ahead of its latest edit'
);

const attachment = (id: string, name = `${id}.png`): ConversationAttachment => ({
  id,
  name,
  mimeType: 'image/png',
  path: `/attachments/${name}`,
  previewUrl: `blob:${id}`
});
assert.deepEqual(
  sessionAttachments(
    [attachment('draft')],
    [attachment('moving')],
    { first: [attachment('sent'), attachment('moving')] }
  ).map((item) => item.id),
  ['draft', 'moving', 'sent'],
  'draft, in-flight, and sent attachments form one de-duplicated session list'
);

console.log('session context tests passed');
