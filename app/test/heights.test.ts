import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { Virtualizer } from '@tanstack/svelte-virtual';
import { applyRow, blocksChars, emptyView, type Item, type Row, type SafeMarkdownBlock } from '../src/lib/rows/store.ts';
import { MARK, collapse, overlap, prependedTop, rowBox } from '../src/lib/rows/heights.ts';
import { MAX_LAG_MS, MIN_CPS, clipBlocks, revealStep } from '../src/lib/rows/reveal.ts';

const fixture = (name: string): Row[] => JSON.parse(readFileSync(new URL(`fixtures/${name}`, import.meta.url), 'utf8')).rows;
const items = (() => {
  const view = emptyView();
  for (const row of [...fixture('rows-text.json'), ...fixture('rows-tools.json')]) applyRow(view, row);
  return view.items;
})();
const flags = { label: false, end: false, open: false, checkout: false };
const WIDTH = 804;
const close = (actual: number, expected: number) => assert.ok(Math.abs(actual - expected) < 1e-9, `${actual} != ${expected}`);
const para = (text: string): SafeMarkdownBlock => ({ kind: 'paragraph', parts: [{ kind: 'text', value: text }] });
const message = (blocks: SafeMarkdownBlock[]): Item => ({ key: 'm', runId: null, kind: 'message', blocks, done: true });

test('rows the CSS fixes have its exact heights, margins included', () => {
  const tool = items.find((item) => item.kind === 'tool')!;
  const thinking = items.find((item) => item.kind === 'thinking')!;
  const stats = items.find((item) => item.kind === 'stats')!;
  // Tool: border 2 + padding 12 + a 13px × 1.45 line, then margin 8.
  const toolBox = rowBox(tool, WIDTH, flags);
  close(toolBox.h, 2 + 12 + 18.85 + 8);
  assert.equal(toolBox.top, 0);
  assert.equal(toolBox.bottom, 8);
  // The label adds its 12px line and 4px; `.end` adds 10px and holds the 8px inside.
  close(rowBox(tool, WIDTH, { ...flags, label: true, end: true }).h, 17.4 + 4 + 40.85 + 10);
  assert.equal(rowBox(tool, WIDTH, { ...flags, end: true }).bottom, 0);
  // Thinking closed: its summary line and margin 8. The turn line: -10 + 17.4 + 18.
  close(rowBox(thinking, WIDTH, flags).h, 20.3 + 8);
  assert.deepEqual(rowBox(stats, WIDTH, flags), MARK);
  close(MARK.h, 25.4);
  close(rowBox({ key: 'o', runId: null, kind: 'other', type: 'x' }, WIDTH, flags).h, 17.4 + 8);
  // A cancelled run draws only its end gap.
  const cancelled: Item = { key: 'e', runId: null, kind: 'error', error: { message: '', code: 'cancelled' } };
  assert.equal(rowBox(cancelled, WIDTH, flags).h, 0);
  assert.equal(rowBox(cancelled, WIDTH, { ...flags, end: true }).h, 10);
  // The checkout line: border 1 + padding 8 + line + margin 12.
  const user: Item = { key: 'u', runId: null, kind: 'user', text: 'hi' };
  close(rowBox(user, WIDTH, { ...flags, checkout: true }).h - rowBox(user, WIDTH, flags).h, 38.4);
});

test('touching margins merge as CSS merges them', () => {
  assert.equal(collapse(10, 16), 16);
  assert.equal(collapse(18, -10), 8);
  assert.equal(collapse(0, -10), -10);
  assert.equal(collapse(-4, -10), -10);
  // A paragraph's 10px over a heading's 16px is one 16px gap: 10 given back.
  assert.equal(overlap(10, 16), 10);
  // The turn line's -10px pulls it up under the reply on its own.
  assert.equal(overlap(8, -10), 0);
  assert.equal(overlap(0, -10), 0);
  // Inside a message: a heading first starts the row with its 16px, unless the label is above it.
  const headed = message([{ kind: 'heading', level: 1, parts: [{ kind: 'text', value: 'Title' }] }, para('Body')]);
  assert.equal(rowBox(headed, WIDTH, flags).top, 16);
  assert.equal(rowBox(headed, WIDTH, { ...flags, label: true }).top, 0);
  // Heading then paragraph: 8 and 0 merge to 8; the paragraph's 10 merges with `.markdown`'s 8.
  close(rowBox(headed, WIDTH, flags).h, 16 + 16.38 * 1.55 + 8 + 21.7 + 10);
});

test('text grows with its lines and wraps sooner when narrower; code stops at 360px', () => {
  const short = rowBox(message([para('Hello')]), WIDTH, flags).h;
  const long = rowBox(message([para('word '.repeat(400))]), WIDTH, flags).h;
  const narrow = rowBox(message([para('word '.repeat(400))]), 400, flags).h;
  assert.ok(short > 0 && long > short && narrow > long, `${short} ${long} ${narrow}`);
  const code = (n: number) => message([{ kind: 'code', language: 'ts', value: 'x;\n'.repeat(n), complete: true }]);
  close(rowBox(code(3), WIDTH, flags).h, 12 + 63 + 3 * 20.8 + 12);
  assert.equal(rowBox(code(500), WIDTH, flags).h, rowBox(code(100), WIDTH, flags).h);
  for (const item of items) for (const width of [0, 320, WIDTH]) assert.ok(rowBox(item, width, flags).h >= 0, item.key);
});

test('rows changing above the reader move the offset by exactly their estimated heights', () => {
  assert.equal(prependedTop(500, [], []), 500);
  assert.equal(prependedTop(500, [], [30, 40.5]), 570.5);
  // The loading line goes out as the page comes in.
  close(prependedTop(500, [25.4], [30, 40]), 500 + 70 - 25.4);

  // Laid out as the virtualizer does (each row at the sum of the sizes above
  // it), the row the reader was on sits at the same distance from the new offset.
  const starts = (sizes: number[]) => sizes.map((_, i) => sizes.slice(0, i).reduce((a, b) => a + b, 0));
  const loaded = [52, 110, 25.4, 80];
  const added = [40, 64.5, 25.4];
  const top = starts(loaded)[2]! + 7; // 7px into the third row
  const after = starts([...added, ...loaded]);
  close(prependedTop(top, [], added) - after[added.length + 2]!, 7);
});

test('the offset applied once the rows are drawn keeps what measuring rows above the reader moved it by', () => {
  // As Conversation.svelte drives the virtualizer: the reader 724px down, at the
  // top of row r7; then the loading line comes in above the rows.
  let keys = Array.from({ length: 10 }, (_, i) => `r${i}`);
  let sizes = keys.map(() => 100);
  const options = () => ({ count: keys.length, getItemKey: (i: number) => keys[i]!, estimateSize: (i: number) => sizes[i]! });
  const v = new Virtualizer<HTMLDivElement, HTMLDivElement>({
    ...options(),
    paddingStart: 24,
    getScrollElement: () => null,
    scrollToFn: () => {},
    observeElementRect: () => {},
    observeElementOffset: () => {},
  });
  v.scrollOffset = 724;
  const readerY = (offset: number) => v.getMeasurements()[keys.indexOf('r7')]!.start - offset;
  assert.equal(readerY(724), 0);

  const pendingTop = prependedTop(724, [], [MARK.h]);
  keys = ['line:loading', ...keys];
  sizes = [MARK.h, ...sizes];
  v.scrollOffset = pendingTop;
  v.setOptions({ ...v.options, ...options() });
  close(readerY(pendingTop), 0);
  // In the same update a row above the reader is drawn and measured for the
  // first time, 20px shorter than estimated: the virtualizer moves the offset
  // up by 20 so the reader's row stays put.
  v.resizeItem(keys.indexOf('r3'), 80);
  close(readerY(v.scrollOffset!), 0);
  // The offset worked out before the rows were drawn would undo that and pull
  // the reader's row up 20px.
  close(readerY(pendingTop), -20);
});

test('the reveal moves at a steady pace when the buffer is small and catches up as it grows', () => {
  const frame = 16;
  const small = revealStep(0, 5, frame);
  assert.ok(small >= 1 && small <= Math.ceil((MIN_CPS * frame) / 1000) + 1, String(small));
  // Bigger buffers move faster, and never take more than MAX_LAG_MS to drain at the current pace.
  let previous = 0;
  for (const backlog of [10, 100, 1_000, 50_000]) {
    const step = revealStep(0, backlog, frame);
    assert.ok(step >= previous, `${backlog}: ${step} < ${previous}`);
    assert.ok((backlog / step) * frame <= MAX_LAG_MS + frame, `${backlog}: ${(backlog / step) * frame}ms behind`);
    previous = step;
  }
  // Never past the text, a long gap catches up at once, and a caught-up reveal holds.
  assert.equal(revealStep(98, 100, 1_000), 100);
  assert.equal(revealStep(0, 400, MAX_LAG_MS), 400);
  assert.equal(revealStep(100, 100, frame), 100);
  assert.equal(revealStep(120, 100, frame), 100);
  let shown = 0;
  let frames = 0;
  while (shown < 2_000 && frames < 1_000) {
    shown = revealStep(shown, 2_000, frame);
    frames++;
  }
  assert.equal(shown, 2_000);
});

test('clipping shows the first characters of the blocks, keeping whole blocks as they are', () => {
  const blocks: SafeMarkdownBlock[] = [
    { kind: 'paragraph', parts: [{ kind: 'text', value: 'Hello ' }, { kind: 'strong', parts: [{ kind: 'text', value: 'world' }] }] },
    { kind: 'list', ordered: false, items: [1, 2, 3].map((n) => ({ task: false, checked: false, parts: [{ kind: 'text' as const, value: `item ${n}` }], blocks: [] })) },
    { kind: 'code', language: 'ts', value: 'const x = 1;', complete: true, lines: [[{ value: 'const x = 1;', className: 'plain' }]] },
  ];
  const total = blocksChars(blocks);
  for (let n = 0; n <= total; n++) assert.equal(blocksChars(clipBlocks(blocks, n)), n, `at ${n}`);
  assert.equal(clipBlocks(blocks, total)[0], blocks[0]);
  assert.equal(clipBlocks(blocks, 0).length, 0);
  const partialCode = clipBlocks(blocks, total - 2).at(-1)!;
  assert.ok(partialCode.kind === 'code' && partialCode.lines === undefined && partialCode.value === 'const x = ');
});
