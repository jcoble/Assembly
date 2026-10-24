import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { compactionLabel, errorNotice } from '../src/lib/chat/notices.ts';

const fixture = (name: string) => JSON.parse(readFileSync(new URL(`fixtures/${name}`, import.meta.url), 'utf8'));

test('a relay error row becomes a notice', () => {
  assert.deepEqual(errorNotice({ message: 'API Error: 529 Overloaded', code: 'overloaded' }), {
    label: 'Conversation error',
    message: 'API Error: 529 Overloaded',
    hint: null,
  });
});

test('a provider error wrapped in JSON is unwrapped, and a full context says what to do', () => {
  const wrapped = JSON.stringify({ code: -32603, message: 'Internal error: Prompt is too long' });
  assert.deepEqual(errorNotice(new Error(wrapped)), {
    label: 'Conversation is full',
    message: "This conversation has outgrown the model's context window, so nothing more can be sent to it.",
    hint: 'Send /compact if this agent offers it, or start a new session and bring what you need with you.',
  });
  const nested = JSON.stringify({ error: { message: 'rate limited' } });
  assert.equal(errorNotice(new Error(nested))?.message, 'rate limited');
});

test('an error with no words gets a sentence, and a cancelled run gets no notice', () => {
  assert.equal(errorNotice(new Error(''))?.message, 'The agent reported an error without saying what went wrong.');
  assert.equal(errorNotice(undefined)?.message, 'The agent reported an error without saying what went wrong.');
  assert.equal(errorNotice(Object.assign(new Error('run cancelled'), { code: 'cancelled' })), null);
});

test('the compaction fixture becomes the boundary label', () => {
  const { chunk } = fixture('compaction.json');
  assert.equal(compactionLabel(chunk.value), 'Context compacted · 152k → 31k');
  assert.equal(compactionLabel({ trigger: 'manual' }), 'Context compacted');
  assert.equal(compactionLabel({ before: 9_400, after: 800 }), 'Context compacted · 9.4k → 800');
  assert.equal(compactionLabel({ before: 1_250_000, after: 200_000 }), 'Context compacted · 1.2m → 200k');
});
