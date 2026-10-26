import assert from 'node:assert/strict';
import { test } from 'node:test';
import { markdownOf, messageTime, toolTitle, turnFolds, workSummary } from '../src/lib/session/fold.ts';
import type { Item, ToolPayload } from '../src/lib/rows/store.ts';
import type { TurnStats } from '../src/lib/session/turns.ts';

const tool = (key: string, kind: string, extra: Partial<ToolPayload> = {}): Item => ({
  key, runId: 'r1', kind: 'tool', itemId: key, tool: { name: kind, state: 'completed', summary: '', hasDetail: false, kind, ...extra },
});
const text = (key: string, runId = 'r1'): Item => ({ key, runId, kind: 'message', blocks: [{ kind: 'paragraph', parts: [{ kind: 'text', value: key }] }], done: true, at: 1 });
const stats = { r1: { runId: 'r1', status: 'completed', durationMs: 12_000 } as unknown as TurnStats };
const turn: Item[] = [
  { key: 'u', runId: 'r1', kind: 'user', text: 'go', at: 1 },
  { key: 'th', runId: 'r1', kind: 'thinking', text: 'hm', done: true },
  text('before'),
  tool('t1', 'edit', { path: 'a.ts' }),
  tool('t2', 'edit', { path: 'a.ts' }),
  tool('t3', 'execute'),
  { key: 'ask', runId: 'r1', kind: 'ask', askId: '1', ask: { kind: 'permission', state: 'open' } },
  { key: 'task', runId: 'r1', kind: 'task', taskId: 'b1', task: { name: 'sleep', state: 'running', canStop: false } },
  text('reply'),
];

test('a finished turn folds its work, not its prompt, reply, open card or running task', () => {
  const fold = turnFolds(turn, stats, null).get('r1')!;
  assert.deepEqual([...fold.work], ['th', 'before', 't1', 't2', 't3']);
  assert.equal(fold.reply, 'reply');
  assert.equal(fold.summary, 'Edited a file, ran a command');
  assert.equal(fold.durationMs, 12_000);
});

test('a running turn, or one without stats yet, never folds', () => {
  assert.equal(turnFolds(turn, stats, 'r1').size, 0);
  assert.equal(turnFolds(turn, {}, null).size, 0);
});

test('a turn that is only its reply has no work to fold, but its reply is known', () => {
  const fold = turnFolds([text('reply')], stats, null).get('r1')!;
  assert.equal(fold.work.size, 0);
  assert.equal(fold.reply, 'reply');
});

test('the summary counts work by kind, distinct files, and failures', () => {
  assert.equal(workSummary([tool('a', 'edit', { path: 'x' }), tool('b', 'edit', { path: 'y' }), tool('c', 'execute'), tool('d', 'execute'), tool('e', 'execute')]), 'Edited 2 files, ran 3 commands');
  assert.equal(workSummary([tool('a', 'read'), tool('b', 'search'), tool('c', 'execute', { state: 'failed' })]), 'Read a file, ran a command, searched once, 1 call failed');
  // A command stopped mid-run reads failed; it still ran.
  assert.equal(workSummary([tool('a', 'execute', { state: 'failed' })]), 'Ran a command, 1 call failed');
  const subagent = { key: 'subagent:s1', runId: 'r1', kind: 'subagent', acpSessionId: 's1', subagent: { sessionId: 'c1', title: 'Explore', state: 'idle', canStop: false } } as const;
  assert.equal(workSummary([subagent]), 'Ran a subagent');
  assert.equal(workSummary([{ key: 't', runId: 'r1', kind: 'thinking', text: '', done: true }]), 'Thought');
  assert.equal(workSummary([tool('a', 'other'), tool('b', 'other')]), 'Used 2 tools');
});

test('the Assembly MCP server tools read as words, in the row and the summary', () => {
  assert.equal(toolTitle('mcp__assembly__start_session'), 'Started a helper');
  assert.equal(toolTitle('mcp.assembly.message_status'), "Checked a helper's answer");
  assert.equal(toolTitle('mcp__other__start_session'), 'mcp__other__start_session');
  const calls = [
    tool('a', 'other', { name: 'mcp__assembly__start_session' }),
    tool('b', 'execute', { name: 'mcp.assembly.message_status' }),
    tool('c', 'execute', { name: 'mcp.assembly.message_status' }),
    tool('d', 'execute', { name: 'mcp.assembly.list_sessions' }),
    tool('e', 'execute'),
  ];
  assert.equal(workSummary(calls), "Ran a command, started a helper, checked answers 2 times, listed sessions");
});

test('a reply copies as markdown', () => {
  assert.equal(
    markdownOf([
      { kind: 'heading', level: 2, parts: [{ kind: 'text', value: 'Done' }] },
      { kind: 'paragraph', parts: [{ kind: 'text', value: 'Ran ' }, { kind: 'code', value: 'ls' }, { kind: 'text', value: ' and ' }, { kind: 'strong', parts: [{ kind: 'text', value: 'it worked' }] }] },
      { kind: 'list', ordered: false, items: [{ task: false, checked: false, parts: [{ kind: 'text', value: 'one' }], blocks: [] }] },
      { kind: 'code', language: 'ts', value: 'x();\n', complete: true },
    ]),
    '## Done\n\nRan `ls` and **it worked**\n\n- one\n\n```ts\nx();\n```',
  );
});

test('a time within the week says its weekday, an older one its date', () => {
  const at = new Date(2026, 9, 22, 12, 2).getTime(); // Thursday
  assert.equal(messageTime(at, at + 60_000, 'en-US'), 'Thursday 12:02 PM');
  assert.equal(messageTime(at, at + 8 * 24 * 3600_000, 'en-US'), 'Oct 22, 12:02 PM');
});
