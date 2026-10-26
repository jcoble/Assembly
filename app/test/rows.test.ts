import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { activeRun, applyRow, emptyView, liveStep, prependPage, renderedChars, runStartedAt, type Row, type View } from '../src/lib/rows/store.ts';
import { pickAnchor, restoredScrollTop, viewUncovered } from '../src/lib/session/scroll.ts';
import { compactionLabel } from '../src/lib/chat/notices.ts';

const fixture = (name: string): Row[] => JSON.parse(readFileSync(new URL(`fixtures/${name}`, import.meta.url), 'utf8')).rows;
const all = [...fixture('rows-text.json'), ...fixture('rows-tools.json')];

// What the relay's page query returns (plan "Paging" and rulings R1-R3): the
// newest row of each (item, kind), a delta hidden once its item has a message
// row, an unfinished message as ONE delta row from 0 with its blocks so far,
// and every row carrying its item's first seq, ordered by it.
function newestForm(rows: Row[]): Row[] {
  const group = (row: Row) => (row.kind === 'delta' || row.kind === 'message' ? 'message' : row.kind);
  const sameItem = (a: Row, b: Row) => a.itemId !== null && a.itemId === b.itemId && group(a) === group(b);
  const kept = rows.flatMap((row): Row[] => {
    const firstSeq = row.itemId === null ? row.seq : Math.min(...rows.filter((r) => sameItem(r, row)).map((r) => r.seq));
    if (row.itemId !== null && rows.some((r) => sameItem(r, row) && r.seq > row.seq && (r.kind === row.kind || r.kind === 'message'))) return [];
    if (row.kind !== 'delta') return [{ ...row, firstSeq }];
    const deltas = rows.filter((r) => sameItem(r, row));
    const folded = live(emptyView(), deltas).items[0]!;
    if (folded.kind !== 'message') throw new Error('not a message');
    const text = deltas.map((r) => (r.kind === 'delta' ? r.payload.text : '')).join('');
    return [{ ...row, firstSeq, payload: { from: 0, text, blocks: folded.blocks } }];
  });
  return kept.sort((a, b) => a.firstSeq! - b.firstSeq!);
}

const live = (view: View, rows: Row[]) => {
  for (const row of rows) applyRow(view, row);
  return view;
};
const keys = (view: View) => view.items.map((item) => item.key);

test('the watermark rule: a row whose prevSeq is the watermark applies, an old one is ignored, any other reloads', () => {
  assert.equal(liveStep(4, { seq: 5, prevSeq: 4 }), 'apply');
  // A finished message's deltas are deleted, so the next row can skip seqs.
  assert.equal(liveStep(4, { seq: 9, prevSeq: 4 }), 'apply');
  assert.equal(liveStep(4, { seq: 4, prevSeq: 3 }), 'ignore');
  assert.equal(liveStep(4, { seq: 2, prevSeq: 1 }), 'ignore');
  assert.equal(liveStep(4, { seq: 6, prevSeq: 5 }), 'reload');
  assert.equal(liveStep(4, { seq: 9, prevSeq: 6 }), 'reload');
  assert.equal(liveStep(0, { seq: 1, prevSeq: 0 }), 'apply');
});

test('a delta keeps the first `from` blocks and replaces the rest; the message row is final', () => {
  const view = emptyView();
  const rows = fixture('rows-text.json');
  live(view, rows.slice(0, 4)); // user, turn, two deltas from 0
  const message = view.items.find((item) => item.kind === 'message')!;
  assert.equal(message.kind, 'message');
  if (message.kind !== 'message') return;
  assert.deepEqual(message.blocks.map((b) => b.kind), ['paragraph', 'table']);
  const first = message.blocks[0];

  applyRow(view, rows[4]!); // from 1: the table grows, a code block opens
  assert.deepEqual(message.blocks.map((b) => b.kind), ['paragraph', 'table', 'code']);
  assert.equal(message.blocks[0], first, 'blocks before `from` are kept as they were');
  assert.equal((message.blocks[1] as { rows: unknown[] }).rows.length, 1);

  applyRow(view, rows[5]!); // from 2: the code block closes with its lines
  const code = message.blocks[2]!;
  assert.ok(code.kind === 'code' && code.complete && code.lines?.length === 2);
  assert.equal(message.done, false);

  applyRow(view, rows[6]!); // the message row
  assert.equal(message.done, true);
  assert.deepEqual(message.blocks.map((b) => b.kind), ['paragraph', 'table', 'code', 'list']);
});

test('a page in newest form, then live rows, folds to the same items as every row live', () => {
  const everyRow = live(emptyView(), all);
  // The page was read when rows up to seq 15 existed; the rest arrived live.
  const view = emptyView();
  prependPage(view, newestForm(all.filter((r) => r.seq <= 15)), false);
  live(view, all.filter((r) => r.seq > 15));
  assert.deepEqual(view.items, everyRow.items);
  assert.deepEqual(keys(view), [
    'user:u1', 'message:m1', 'seq:9',
    'user:u2', 'thinking:th1', 'tool:tool1', 'tool:tool2', 'tool:tool3', 'tool:tool4', 'message:m2', 'seq:25', 'seq:27',
  ]);
  // The whole thread read as one page folds the same way.
  assert.deepEqual(prependPage(emptyView(), newestForm(all), false).items, everyRow.items);
});

test('a page read mid-message holds it as one delta from 0, and live deltas continue it', () => {
  const everyRow = live(emptyView(), all);
  const page = newestForm(all.filter((r) => r.seq <= 5));
  const m1 = page.filter((r) => r.itemId === 'm1');
  assert.equal(m1.length, 1);
  assert.ok(m1[0]!.kind === 'delta' && m1[0]!.payload.from === 0 && m1[0]!.payload.blocks.length === 3);
  const view = prependPage(emptyView(), page, false);
  live(view, all.filter((r) => r.seq > 5)); // the next delta is from 2
  assert.deepEqual(view.items, everyRow.items);
});

test('a page places items by their first seq, as live rows do', () => {
  // Tool A starts, message B streams and ends, then A completes.
  const rows: Row[] = [
    { sessionId: 't', seq: 1, runId: 'r', itemId: 'A', kind: 'tool', payload: { name: 'Bash', state: 'started', summary: 'a', hasDetail: false }, createdAt: 0 },
    { sessionId: 't', seq: 2, runId: 'r', itemId: 'B', kind: 'delta', payload: { from: 0, text: 'b', blocks: [] }, createdAt: 0 },
    { sessionId: 't', seq: 3, runId: 'r', itemId: 'B', kind: 'message', payload: { blocks: [] }, createdAt: 0 },
    { sessionId: 't', seq: 4, runId: 'r', itemId: 'A', kind: 'tool', payload: { name: 'Bash', state: 'completed', summary: 'a', hasDetail: true }, createdAt: 0 },
  ];
  assert.deepEqual(keys(live(emptyView(), rows)), ['tool:A', 'message:B']);
  const page = newestForm(rows);
  assert.deepEqual(page.map((r) => [r.seq, r.firstSeq]), [[4, 1], [3, 2]]);
  // Folded by firstSeq even when handed over in seq order.
  const bySeq = [...page].sort((a, b) => a.seq - b.seq);
  assert.deepEqual(keys(prependPage(emptyView(), bySeq, false)), ['tool:A', 'message:B']);
  assert.equal(prependPage(emptyView(), bySeq, true).oldest, 1, 'the next page is before the lowest first seq');
});

test('the active run: the page\'s activeRunId or a sent run, until a turn row ends it', () => {
  // Reopened mid-run: the run's started row is older than this page.
  const page = { rows: newestForm(all).filter((r) => r.seq >= 20 && r.seq <= 24), hasBefore: true, activeRunId: 'r2' };
  const view = prependPage(emptyView(), page.rows, page.hasBefore);
  assert.equal(view.turn, null);
  assert.equal(activeRun(view, page.activeRunId), 'r2');
  assert.equal(activeRun(view, null), null);
  live(view, all.filter((r) => r.seq >= 25)); // error, turn failed, stats
  assert.equal(activeRun(view, page.activeRunId), null);

  // A run just sent is active before its first turn row arrives, and while it is started.
  const idle = live(emptyView(), fixture('rows-text.json'));
  assert.equal(activeRun(idle, null), null);
  assert.equal(activeRun(idle, 'r2'), 'r2');
  live(idle, all.filter((r) => r.seq >= 10 && r.seq <= 11));
  assert.equal(activeRun(idle, 'r2'), 'r2');
  assert.equal(activeRun(idle, null), 'r2');
});

test('a sent run that ended stays ended when a run the agent continues by itself ends after it', () => {
  // As recorded: a prompt that leaves a background command running ends its run (r1); the command
  // finishing starts a run of the agent's own (r2), which ends too. Nothing is running.
  const stats = (runId: string) => ({ runId, status: 'completed', startedAt: 0, durationMs: 1, firstTokenMs: 1, model: 'claude-opus-5-5', inputTokens: 4, outputTokens: 121, cacheHitPct: 94.9, costUsd: 0.05, toolCalls: 1, branch: null, head: null, prevBranch: null, prevHead: null });
  const row = (seq: number, runId: string, kind: string, payload: unknown, itemId: string | null = null) => ({ sessionId: 't', seq, runId, itemId, kind, payload, createdAt: seq }) as Row;
  const view = live(emptyView(), [
    row(1, 'r1', 'user', { text: 'Run it in the background, then reply "started".' }),
    row(2, 'r1', 'turn', { state: 'started' }),
    row(3, 'r1', 'stats', stats('r1')),
    row(4, 'r1', 'turn', { state: 'finished' }),
    row(5, 'r1', 'context', { used: 43115, size: 1000000 }, 'context:r1'),
    row(6, 'r2', 'turn', { state: 'started', reason: 'Continued after a background task finished' }),
    row(7, 'r2', 'stats', stats('r2')),
    row(8, 'r2', 'turn', { state: 'finished' }),
  ]);
  assert.equal(activeRun(view, 'r1'), null);
});

test('a running turn counts from its turn row, so reopening it mid-turn keeps its time', () => {
  const started: Row = { sessionId: 't', seq: 7, runId: 'r2', itemId: null, kind: 'turn', payload: { state: 'started' }, createdAt: 1_000 };
  const view = prependPage(emptyView(), [started], false, 0);
  assert.equal(runStartedAt(view, 'r2'), 1_000);
  // A run just sent has no turn row yet; neither does an ended one.
  assert.equal(runStartedAt(view, 'r3'), null);
  applyRow(view, { ...started, seq: 8, payload: { state: 'finished' }, createdAt: 9_000 });
  assert.equal(runStartedAt(view, 'r2'), null);
});

test('the fold carries tool state, the plan, turn state and stats', () => {
  const view = live(emptyView(), all.filter((r) => r.seq <= 20));
  assert.equal(view.turn?.state, 'started');
  assert.equal(view.turn?.runId, 'r2');
  const tools = view.items.filter((item) => item.kind === 'tool');
  assert.deepEqual(tools.map((t) => t.kind === 'tool' && [t.tool.name, t.tool.state]), [
    ['Bash', 'completed'], ['Read', 'failed'], ['Edit', 'completed'], ['TodoWrite', 'started'],
  ]);
  const edit = tools[2]!;
  assert.ok(edit.kind === 'tool' && edit.tool.added === 3 && edit.tool.removed === 1 && edit.tool.hasDetail);
  assert.equal(view.plan, null);

  live(view, all.filter((r) => r.seq > 20));
  assert.deepEqual(view.plan?.items.map((s) => s.status), ['completed', 'in_progress']);
  assert.equal(view.turn?.state, 'failed');
  assert.equal(view.stats['r1']?.status, 'completed');
  assert.equal(view.stats['r2']?.prevHead, '7fd1a60b01f91b314f59955a4e4d4e80d8edf11d');
  const error = view.items.find((item) => item.kind === 'error');
  assert.ok(error?.kind === 'error' && error.error.message === 'API Error: 529 Overloaded' && error.runId === 'r2');
  const user = view.items.find((item) => item.key === 'user:u2');
  assert.ok(user?.kind === 'user' && user.runId === 'r2' && user.text.includes('Attached image: '));
});

test('an older page goes in above, keeps every loaded item, and the anchor holds', () => {
  const view = emptyView();
  prependPage(view, newestForm(all).filter((r) => r.seq >= 10), true);
  assert.equal(view.oldest, 10);
  assert.equal(view.hasBefore, true);
  const loaded = [...view.items];
  const plan = view.plan;

  // Laid out top to bottom, 100px each; the view's top cuts the third row.
  const rowsAt = (scrollTop: number) => view.items.map((item, i) => ({ id: item.key, top: i * 100 - scrollTop, bottom: i * 100 + 100 - scrollTop }));
  const scrollTop = 250;
  const anchor = pickAnchor(rowsAt(scrollTop), 0)!;
  assert.deepEqual(anchor, { id: loaded[2]!.key, offset: -50 });

  prependPage(view, newestForm(all).filter((r) => r.seq < 10), false);
  assert.equal(view.oldest, 1);
  assert.equal(view.hasBefore, false);
  assert.deepEqual(keys(view).slice(0, 3), ['user:u1', 'message:m1', 'seq:9']);
  assert.deepEqual(view.items.slice(3), loaded, 'every loaded item is kept, as the same object');
  assert.equal(view.plan, plan, 'an older page never replaces a newer plan');
  assert.equal(view.turn?.state, 'failed', 'nor the newer turn state');

  const moved = rowsAt(scrollTop).find((r) => r.id === anchor.id)!;
  const next = restoredScrollTop(scrollTop, anchor.offset, moved.top);
  assert.equal(rowsAt(next).find((r) => r.id === anchor.id)!.top, anchor.offset);
});

test('a row of an item already loaded in a newer form is skipped by an older page', () => {
  const view = live(emptyView(), all.filter((r) => r.seq >= 14 && r.seq <= 17));
  prependPage(view, all.filter((r) => r.seq === 14), true); // tool1 started, older than its completed row
  const tool1 = view.items.filter((item) => item.key === 'tool:tool1');
  assert.equal(tool1.length, 1);
  assert.ok(tool1[0]!.kind === 'tool' && tool1[0]!.tool.state === 'completed');
});

test('rendered characters count the text the conversation shows', () => {
  const view = live(emptyView(), fixture('rows-text.json'));
  // user text + paragraph + table cells + code + list items
  const expected = 'Show me a table and some code'.length + 'Here is a table:'.length + 'filelinesREADME1'.length +
    'const x = 1;\n// done'.length + 'read itdocs'.length;
  assert.equal(renderedChars(view), expected);
});

test('a compaction row becomes a compaction item, drawn with its token counts', () => {
  const view = live(emptyView(), fixture('rows-compaction.json'));
  const item = view.items[0]!;
  assert.deepEqual(view.items, [{ key: 'seq:12', runId: 'r2', kind: 'compaction', trigger: 'auto', preTokens: 152340, postTokens: 31200 }]);
  assert.ok(item.kind === 'compaction');
  assert.equal(compactionLabel({ before: item.preTokens, after: item.postTokens }), 'Context compacted · 152k → 31k');
  // No post_tokens from the SDK: the line Assembly draws with no counts.
  assert.equal(compactionLabel({ before: item.preTokens, after: undefined }), 'Context compacted');
});

test('a task row stays one item as its state changes, in the run it started in', () => {
  const rows: Row[] = [
    { sessionId: 't', seq: 1, runId: 'r1', itemId: 'task:b1', kind: 'task', payload: { name: 'Background command', state: 'running', canStop: false, toolCallId: 't1' }, createdAt: 0 },
    { sessionId: 't', seq: 2, runId: 'r1', itemId: 'task:b1', kind: 'task', payload: { name: 'Background command', state: 'completed', canStop: false, toolCallId: 't1' }, createdAt: 0 },
  ];
  const view = live(emptyView(), rows);
  assert.deepEqual(keys(view), ['task:task:b1']);
  const task = view.items[0]!;
  if (task.kind !== 'task') throw new Error('not a task');
  assert.equal(task.taskId, 'b1');
  assert.equal(task.task.state, 'completed');
});

test('a subagent row stays one item, keyed by its child session, as its parent reports on it', () => {
  const rows: Row[] = [
    { sessionId: 't', seq: 1, runId: 'r1', itemId: 'subagent:s2', kind: 'subagent', payload: { sessionId: 'c1', title: 'Explore', state: 'running', canStop: true }, createdAt: 0 },
    { sessionId: 't', seq: 2, runId: 'r1', itemId: 'subagent:s2', kind: 'subagent', payload: { sessionId: 'c1', title: 'Explore', state: 'idle', stopReason: 'end_turn', canStop: false, toolCallId: 't1' }, createdAt: 0 },
  ];
  const view = live(emptyView(), rows);
  assert.deepEqual(keys(view), ['subagent:subagent:s2']);
  const subagent = view.items[0]!;
  if (subagent.kind !== 'subagent') throw new Error('not a subagent');
  assert.equal(subagent.acpSessionId, 's2');
  assert.equal(subagent.subagent.state, 'idle');
  assert.equal(subagent.subagent.toolCallId, 't1');
});

test('a run the agent started itself shows its reason where a prompt would be', () => {
  const rows: Row[] = [
    { sessionId: 't', seq: 1, runId: 'r2', itemId: null, kind: 'turn', payload: { state: 'started', reason: 'Continued after a background task finished' }, createdAt: 0 },
    { sessionId: 't', seq: 2, runId: 'r2', itemId: null, kind: 'turn', payload: { state: 'finished' }, createdAt: 0 },
  ];
  const view = live(emptyView(), rows);
  assert.deepEqual(view.items.map((item) => (item.kind === 'note' ? item.text : item.kind)), ['Continued after a background task finished']);
  assert.equal(view.turn?.state, 'finished');
});

test('an ask row opens a card that its settled row updates in place', () => {
  const options = [{ optionId: 'allow', name: 'Allow', kind: 'allow_once' }];
  const rows: Row[] = [
    { sessionId: 't', seq: 1, runId: 'r', itemId: 'ask:7', kind: 'ask', payload: { kind: 'permission', state: 'open', title: 'Run ls', options }, createdAt: 0 },
    { sessionId: 't', seq: 2, runId: 'r', itemId: 'ask:7', kind: 'ask', payload: { kind: 'permission', state: 'answered', title: 'Run ls', options, answer: { optionId: 'allow' } }, createdAt: 0 },
  ];
  const view = live(emptyView(), rows);
  assert.deepEqual(keys(view), ['ask:ask:7']);
  const card = view.items[0]!;
  if (card.kind !== 'ask') throw new Error('not an ask');
  assert.equal(card.askId, '7');
  assert.equal(card.ask.state, 'answered');
});

test('the newest context report wins, live or paged, and draws no item', () => {
  const context = (seq: number, used: number): Row => ({ sessionId: 't', seq, runId: `run-${seq}`, itemId: `context:run-${seq}`, kind: 'context', payload: { used, size: 1_000 }, createdAt: seq });
  const view = emptyView();
  applyRow(view, context(5, 100));
  applyRow(view, context(9, 300));
  assert.deepEqual(view.context, { seq: 9, used: 300, size: 1_000 });
  assert.deepEqual(view.items, []);
  prependPage(view, [context(2, 50)], false);
  assert.equal(view.context?.used, 300);
  assert.equal(prependPage(emptyView(), [context(2, 50)], false).context?.used, 50);
});

test('a view the drawn rows miss is uncovered: the virtualizer kept an offset past the end of a shrunk list', () => {
  const rows = [{ start: 3000, end: 3200 }, { start: 3200, end: 3500 }];
  assert.equal(viewUncovered(rows, 3100, 350), false);
  // The list shrank and scrollTop was clamped to 1400; the rows are still those for the old offset.
  assert.equal(viewUncovered(rows, 1400, 770), true);
  assert.equal(viewUncovered([], 0, 770), true);
  assert.equal(viewUncovered(rows, 3600, 770), true);
  // A band left empty between drawn rows: the window drew from "Guardian Review" down with 190 px blank above it.
  assert.equal(viewUncovered([{ start: 0, end: 100 }, { start: 290, end: 900 }], 0, 770), true);
  assert.equal(viewUncovered([{ start: 0, end: 100 }, { start: 120, end: 900 }], 0, 770), false);
  // The list ends inside the view: only the part above its end has to be covered.
  assert.equal(viewUncovered([{ start: 0, end: 400 }], 0, 400), false);
});
