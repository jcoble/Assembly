/**
 * topTabsOps.ts — the rules for the top tab row, as plain functions.
 *
 * PURE: no store, no DOM. The row mixes three kinds of tab. Editor and browser
 * tabs belong to the editor and browser stores (a file being open IS its tab),
 * while Changes, History and Pull requests are single tabs that exist only
 * because the row says so. The row therefore keeps just an order of keys and
 * reads the editor and browser lists live, so it never disagrees with them.
 */

export type TopTabKind = 'editor' | 'browser' | 'diff' | 'git-history' | 'pull-requests';
export type SingletonTabKind = 'diff' | 'git-history' | 'pull-requests';
/** 'editor:<absolute path>' | 'browser:<tab id>' | 'diff' | 'git-history' | 'pull-requests' */
export type TopTabKey = string;
/** `id` is the file path, the browser tab id, or the kind itself for a single tab. */
export interface TopTabRef { kind: TopTabKind; id: string }
export interface LiveTabSources { editorPaths: readonly string[]; browserTabIds: readonly string[] }
export interface SessionTopTabsWorkspace { order: TopTabKey[]; activeKey: TopTabKey | null }

const SINGLETON_KINDS: readonly string[] = ['diff', 'git-history', 'pull-requests'];

export function topTabKey(ref: TopTabRef): TopTabKey {
  return ref.kind === 'editor' || ref.kind === 'browser' ? `${ref.kind}:${ref.id}` : ref.kind;
}

/** Splits on the first ':' only, so a path that contains ':' survives. */
export function parseTopTabKey(key: string): TopTabRef | null {
  if (SINGLETON_KINDS.includes(key)) return { kind: key as SingletonTabKind, id: key };
  const split = key.indexOf(':');
  if (split <= 0) return null;
  const kind = key.slice(0, split);
  const id = key.slice(split + 1);
  if ((kind !== 'editor' && kind !== 'browser') || !id) return null;
  return { kind, id };
}

/** True when the tab's content still exists. Single tabs always do. */
function isLive(ref: TopTabRef, live: LiveTabSources): boolean {
  if (ref.kind === 'editor') return live.editorPaths.includes(ref.id);
  if (ref.kind === 'browser') return live.browserTabIds.includes(ref.id);
  return true;
}

/**
 * The row on screen: the stored order filtered to tabs that still exist, then
 * newly opened files in the editor's order, then new browser pages in the
 * browser's order.
 */
export function topTabRow(order: readonly TopTabKey[], live: LiveTabSources): TopTabKey[] {
  const row: TopTabKey[] = [];
  for (const key of order) {
    const ref = parseTopTabKey(key);
    if (ref && isLive(ref, live) && !row.includes(key)) row.push(key);
  }
  for (const path of live.editorPaths) {
    const key = topTabKey({ kind: 'editor', id: path });
    if (!row.includes(key)) row.push(key);
  }
  for (const id of live.browserTabIds) {
    const key = topTabKey({ kind: 'browser', id });
    if (!row.includes(key)) row.push(key);
  }
  return row;
}

/** The chosen tab when it is in the row, else the last tab, else nothing. */
export function resolveActiveKey(row: readonly TopTabKey[], chosen: TopTabKey | null): TopTabKey | null {
  return chosen !== null && row.includes(chosen) ? chosen : row.at(-1) ?? null;
}

export function openTopTab(row: readonly TopTabKey[], key: TopTabKey): TopTabKey[] {
  return row.includes(key) ? [...row] : [...row, key];
}

/** Closing the active tab moves to its right neighbour, else its left, else nothing. */
export function closeTopTab(
  row: readonly TopTabKey[],
  active: TopTabKey | null,
  key: TopTabKey
): { order: TopTabKey[]; activeKey: TopTabKey | null } {
  const index = row.indexOf(key);
  const order = row.filter((entry) => entry !== key);
  if (index < 0 || active !== key) return { order, activeKey: active };
  return { order, activeKey: row[index + 1] ?? row[index - 1] ?? null };
}

/** A stored record, or undefined when it is not one. Tabs whose content is gone are dropped. */
export function normalizeTopTabs(value: unknown, live: LiveTabSources): SessionTopTabsWorkspace | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const entry = value as Record<string, unknown>;
  const order: TopTabKey[] = [];
  if (Array.isArray(entry.order)) {
    for (const key of entry.order) {
      if (typeof key !== 'string' || order.includes(key)) continue;
      const ref = parseTopTabKey(key);
      if (ref && isLive(ref, live)) order.push(key);
    }
  }
  const activeKey = typeof entry.activeKey === 'string' && order.includes(entry.activeKey)
    ? entry.activeKey
    : null;
  return { order, activeKey };
}
