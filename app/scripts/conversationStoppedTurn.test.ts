import assert from 'node:assert/strict';
import type { AgentConversationTurnFacts } from '../src/lib/shell/conversation/conversationTypes.ts';
import { conversationTurnGroups, turnRows, type ConversationDisplayItem } from '../src/lib/shell/conversation/conversationTimeline.ts';

// The turn's end comes from its facts, which a live turn and a turn read back
// from the store both carry, so the Stopped line looks the same either way.
const prompt = (turnId: string): ConversationDisplayItem => ({ kind: 'user', itemId: `prompt:${turnId}`, text: 'Write a long essay', turnId, timestampMs: 1 } as ConversationDisplayItem);
const reply = (turnId: string, itemId = `reply:${turnId}`): ConversationDisplayItem => ({ kind: 'assistant', itemId, text: 'and fermented t', turnId, timestampMs: 2, completed: true } as ConversationDisplayItem);
const ended = (turnId: string, terminalState: string): AgentConversationTurnFacts => ({ turnId, startedAtMs: 1, endedAtMs: 4, terminalState });
const lastRows = (items: ConversationDisplayItem[], facts: AgentConversationTurnFacts[]) =>
  conversationTurnGroups(items, null, facts).map((group) => turnRows(group, group.items, undefined).at(-1));

// A reply the owner stopped is followed by a Stopped line.
{
  const [last] = lastRows([prompt('a'), reply('a')], [ended('a', 'interrupted')]);
  assert.ok(last && last !== 'heading' && last.kind === 'unknown', 'the stopped turn ends with a marker row');
  assert.equal(last.kind === 'unknown' && last.text, 'Stopped');
  assert.equal(last.turnId, 'a', 'the line belongs to the turn it ended');
}

// A turn that finished, failed or is still running draws no extra line.
for (const facts of [[ended('a', 'completed')], [ended('a', 'failed')], []]) {
  const [last] = lastRows([prompt('a'), reply('a')], facts);
  assert.equal(last !== 'heading' && last?.itemId, 'reply:a', `turn ${facts[0]?.terminalState ?? 'running'}`);
}

// When another turn's rows split a stopped turn in two, the line shows once, after its last part.
{
  const rows = lastRows([prompt('a'), reply('a', 'early'), prompt('b'), reply('a', 'late')], [ended('a', 'interrupted'), ended('b', 'completed')]);
  const stopped = rows.filter((row) => row !== 'heading' && row?.kind === 'unknown');
  assert.equal(stopped.length, 1, 'one Stopped line');
  assert.equal(rows.at(-1), stopped[0], 'after the last part of the stopped turn');
}

console.log('conversation stopped turn tests passed');
