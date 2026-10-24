import assert from 'node:assert/strict';
import { test } from 'node:test';
import { PREFETCH_PX, pickAnchor, restoredScrollTop, shouldLoadOlder } from '../src/lib/thread/scroll.ts';

// Rows laid out top to bottom, as offsets from the viewport's top for a scroller at `scrollTop`.
function layout(heights: number[], scrollTop: number, ids: string[]) {
  let y = -scrollTop;
  return heights.map((h, i) => {
    const row = { id: ids[i]!, top: y, bottom: y + h };
    y += h;
    return row;
  });
}

test('prepending older rows keeps the anchor row at the same offset', () => {
  const ids = ['m3', 'm4', 'm5', 'm6'];
  const heights = [300, 120, 500, 80];
  const scrollTop = 350; // m3 is above the view, m4 straddles its top
  const before = pickAnchor(layout(heights, scrollTop, ids), 0);
  assert.deepEqual(before, { id: 'm4', offset: -50 });

  // Two older rows land on top. Without a correction the browser keeps scrollTop, so every row moves down 700px.
  const grown = layout([400, 300, ...heights], scrollTop, ['m1', 'm2', ...ids]);
  const moved = grown.find((row) => row.id === before!.id)!;
  const next = restoredScrollTop(scrollTop, before!.offset, moved.top);
  assert.equal(next, 1050);
  const after = layout([400, 300, ...heights], next, ['m1', 'm2', ...ids]).find((row) => row.id === 'm4')!;
  assert.equal(after.top, before!.offset);
});

test('a browser that already kept the anchor (column-reverse) gets no correction', () => {
  assert.equal(restoredScrollTop(-200, 40, 40), -200);
});

test('the anchor is the first row still showing at the top', () => {
  assert.equal(pickAnchor([], 0), null);
  assert.deepEqual(pickAnchor(layout([100, 100], 100, ['a', 'b']), 0), { id: 'b', offset: 0 });
});

test('the older page is asked for within the prefetch distance, before the top is reached', () => {
  const ready = { hasOlder: true, loading: false };
  assert.equal(shouldLoadOlder(PREFETCH_PX + 1, ready), false);
  assert.equal(shouldLoadOlder(PREFETCH_PX, ready), true);
  assert.equal(shouldLoadOlder(400, ready), true);
  // A list shorter than the view sits below its top: keep loading until it fills.
  assert.equal(shouldLoadOlder(-300, ready), true);
  assert.equal(shouldLoadOlder(0, { hasOlder: false, loading: false }), false);
  assert.equal(shouldLoadOlder(0, { hasOlder: true, loading: true }), false);
});
