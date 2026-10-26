// History paging over the loaded messages. Older pages are asked for this far
// before the top, so they land while the reader is still scrolling toward them.
export const PREFETCH_PX = 1200;

// `distanceFromTop` is how much content sits above the view's top edge. It is
// negative when the list is shorter than the view.
export function shouldLoadOlder(distanceFromTop: number, state: { hasOlder: boolean; loading: boolean }): boolean {
  return state.hasOlder && !state.loading && distanceFromTop <= PREFETCH_PX;
}

// The first row still showing at the view's top, and its offset from that top.
// Row positions are relative to the view's top edge.
export function pickAnchor(
  rows: ReadonlyArray<{ id: string; top: number; bottom: number }>,
  viewTop: number,
): { id: string; offset: number } | null {
  const row = rows.find((r) => r.bottom > viewTop);
  return row ? { id: row.id, offset: row.top - viewTop } : null;
}

// The scrollTop that puts the anchor row back where it was after rows were
// added above it. Works whether scrollTop counts from the top or, in a
// column-reverse scroller, from the bottom.
export function restoredScrollTop(scrollTop: number, offsetBefore: number, offsetAfter: number): number {
  return scrollTop + (offsetAfter - offsetBefore);
}

// Whether the drawn rows leave part of the view [top, top + height] empty: a gap above the first,
// below the last, or between two of them wider than the margins rows leave (GAP px).
const GAP = 48;
export function viewUncovered(rows: { start: number; end: number }[], top: number, height: number): boolean {
  if (height <= 0) return false;
  const shown = rows.filter((r) => r.end > top && r.start < top + height).sort((a, b) => a.start - b.start);
  if (!shown.length) return true;
  if (shown[0]!.start > top + GAP) return true;
  let reached = shown[0]!.end;
  for (const r of shown.slice(1)) {
    if (r.start > reached + GAP) return true;
    reached = Math.max(reached, r.end);
  }
  return reached < top + height - GAP;
}
