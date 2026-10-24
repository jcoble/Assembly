/**
 * explorerTree.ts — the file explorer's PURE view maths.
 *
 * Everything the explorer panel needs to turn "a flat list of scanned files"
 * into "the rows visible in a scrolling tree" lives here: no Svelte, no runes,
 * no backend, no DOM. That is the point — the rune store cannot be loaded by a
 * plain Node test (`$state` is a compiler rune), so every rule worth testing is
 * kept in this file and covered by `scripts/explorerStore.test.mjs`.
 *
 * The tree building, flattening and windowing themselves are NOT reimplemented:
 * they are the audited helpers in `src/lib/sourceData.ts`, imported as-is.
 * This module only composes them and owns the small decisions around them
 * (row height, how far to overscan, which folders a reveal has to open).
 */
import {
  buildSourceTree,
  filterSourceRecords,
  flattenSourceTree,
  folderIdsForSourceRecord,
  scrollTopForSourceTreeReveal,
  virtualizeSourceTreeRows,
  type SourceRecord,
  type SourceTreeRow,
  type VirtualSourceTreeRows
} from '../../../sourceData.ts';

/** Height of one tree row in pixels. The CSS and the maths must agree exactly:
 * the windowing below places rows by multiplying this number, so a row that
 * renders taller than this drifts out of its slot. */
export const EXPLORER_ROW_HEIGHT = 24;

/** Rows kept rendered above and below the visible window, so a fast scroll
 * does not flash empty space before the next frame lands. */
export const EXPLORER_OVERSCAN_ROWS = 8;

/** Viewport height assumed before the panel has ever been measured — it mounts
 * inside a hidden parking area where its real height is zero, and windowing
 * against zero would render a single row. */
export const EXPLORER_FALLBACK_VIEWPORT_HEIGHT = 420;

export interface ExplorerViewInput {
  /** Every file the last scan returned. */
  records: SourceRecord[];
  /** The filter box text. Empty means "show the whole tree". */
  query: string;
  /** Ids of folders the user has opened (`folder:<relative path>`). */
  expandedFolderIds: ReadonlySet<string>;
  /** Current scroll offset of the tree container, in pixels. */
  scrollTop: number;
  /** Measured height of the tree container, in pixels. */
  viewportHeight: number;
  rowHeight?: number;
  overscanRows?: number;
}

export interface ExplorerView {
  /** Records left after the filter box. */
  matchedRecords: SourceRecord[];
  /** Every row the tree would show if it could render all of them at once. */
  rows: SourceTreeRow[];
  /** The slice actually rendered, plus the spacer heights around it. */
  virtual: VirtualSourceTreeRows;
  /** True while filtering: a filtered tree opens every folder on its own, so a
   * match nested six levels down is visible without any clicking. */
  autoExpandFolders: boolean;
}

/** Filter → tree → visible rows → rendered window, in one pass. */
export function buildExplorerView(input: ExplorerViewInput): ExplorerView {
  const matchedRecords = filterSourceRecords(input.records, input.query);
  const autoExpandFolders = input.query.trim().length > 0;
  const rows = flattenSourceTree(
    buildSourceTree(matchedRecords),
    input.expandedFolderIds as Set<string>,
    autoExpandFolders
  );
  const virtual = virtualizeSourceTreeRows(
    rows,
    input.scrollTop,
    input.viewportHeight > 0 ? input.viewportHeight : EXPLORER_FALLBACK_VIEWPORT_HEIGHT,
    input.rowHeight ?? EXPLORER_ROW_HEIGHT,
    input.overscanRows ?? EXPLORER_OVERSCAN_ROWS
  );

  return { matchedRecords, rows, virtual, autoExpandFolders };
}

/** Open a closed folder / close an open one. Returns a NEW set: the store keeps
 * expansion in a plain `Set`, which Svelte does not track in place — only the
 * assignment of a fresh set makes the tree redraw. */
export function toggleFolder(expandedFolderIds: ReadonlySet<string>, folderId: string): Set<string> {
  const next = new Set(expandedFolderIds);
  if (next.has(folderId)) {
    next.delete(folderId);
  } else {
    next.add(folderId);
  }
  return next;
}

/** Every folder that has to be open for `record` to be a visible row, added to
 * what is already open. Used when something else (the palette, the editor)
 * asks the explorer to show a particular file. */
export function expandedForRecord(
  expandedFolderIds: ReadonlySet<string>,
  record: SourceRecord
): Set<string> {
  const next = new Set(expandedFolderIds);
  for (const folderId of folderIdsForSourceRecord(record)) next.add(folderId);
  return next;
}

/** Position of a file's row among the visible rows, or -1 when it is not one
 * (its folder is closed, or the filter box excludes it). */
export function rowIndexForPath(rows: SourceTreeRow[], path: string): number {
  return rows.findIndex((row) => row.node.file?.path === path);
}

/** Scroll offset that brings a file's row into view, or the current offset
 * unchanged when the row is already visible or not shown at all. */
export function scrollTopForPath(
  rows: SourceTreeRow[],
  path: string,
  scrollTop: number,
  viewportHeight: number,
  rowHeight: number = EXPLORER_ROW_HEIGHT
): number {
  const rowIndex = rowIndexForPath(rows, path);
  if (rowIndex < 0) return scrollTop;
  return scrollTopForSourceTreeReveal(
    rowIndex,
    scrollTop,
    viewportHeight > 0 ? viewportHeight : EXPLORER_FALLBACK_VIEWPORT_HEIGHT,
    rowHeight
  );
}

/** Last path segment of a project root, for the panel's heading. */
export function projectRootLabel(root: string | null): string {
  if (!root) return '';
  const segments = root.split('/').filter(Boolean);
  return segments[segments.length - 1] ?? root;
}
