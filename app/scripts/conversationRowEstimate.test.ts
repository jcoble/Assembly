/**
 * conversationRowEstimate.test.ts — the height the transcript guesses for a row
 * before it is measured. A close guess keeps the scroll position from jumping
 * when a row scrolls into view and is measured.
 *
 * Run: node --experimental-strip-types scripts/conversationRowEstimate.test.ts
 */
import assert from 'node:assert/strict';
import { estimateRowHeight } from '../src/lib/shell/conversation/conversationScrollAnchor.ts';
import type { ConversationDisplayItem } from '../src/lib/shell/conversation/conversationTimeline.ts';

const message = (kind: 'user' | 'assistant', length: number): ConversationDisplayItem =>
  ({ kind, itemId: `${kind}-${length}`, text: 'x'.repeat(length), timestampMs: 0 });

// message_row_estimates_follow_text_length
{
  // A reply grows with its text, between one short line and a cap.
  assert.equal(estimateRowHeight({ item: message('assistant', 30) }), 48);
  assert.equal(estimateRowHeight({ item: message('assistant', 1500) }), 500);
  assert.equal(estimateRowHeight({ item: message('assistant', 4500) }), 1500);
  assert.equal(estimateRowHeight({ item: message('assistant', 60_000) }), 2000);
  // A sent message folds after ten lines, so a long one is guessed at its folded height.
  assert.equal(estimateRowHeight({ item: message('user', 300) }), 100);
  assert.equal(estimateRowHeight({ item: message('user', 6000) }), 300);
  // Tool calls and tool runs are one line; other rows keep the general guess.
  const tool: ConversationDisplayItem = { kind: 'tool', itemId: 't', title: 'Ran', toolKind: 'command', state: 'completed', output: 'y'.repeat(9000), timestampMs: 0 };
  assert.equal(estimateRowHeight({ item: tool }), 36);
  assert.equal(estimateRowHeight({ run: {} }), 36);
  assert.equal(estimateRowHeight({ item: { kind: 'reasoning', itemId: 'r', text: 'z'.repeat(9000), timestampMs: 0 } }), 90);
  assert.equal(estimateRowHeight({}), 90);
}

console.log('conversationRowEstimate.test.ts passed');
