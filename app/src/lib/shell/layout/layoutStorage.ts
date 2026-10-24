/**
 * layoutStorage.ts — pure persistence helpers for the /next shell layout.
 *
 * No DOM, no dockview import, no IO of its own: callers hand in a Storage-like
 * object so node tests can run without a browser. Every function is total —
 * corrupt JSON, quota errors, and malformed trees come back as null / false /
 * empty, never as a throw into the shell.
 */

/** The subset of `Storage` the shell needs (injectable for tests). */
export interface LayoutStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

/**
 * Bumped to `-v3` when the far-right icon rail was deleted and the shell went
 * down to three columns plus the bottom dock. A layout saved under an older key
 * describes a region that no longer exists, and restoring it would put the grid
 * back together around a column nothing draws. There is no honest way to
 * translate one into the other — the old layout says nothing about how wide the
 * user wants a column that was never there — so it is not migrated. It is left
 * where it is, harmless and unread, and the new arrangement starts from its
 * defaults. The same treatment `-v2` gave the arrangement before it.
 */
export const GRID_LAYOUT_KEY = 'mac-command-bar.next.grid-layout-v3';

/**
 * Bumped to `-v6` when the center pane went down to Session, Editor and Diff.
 * The browser, the session history and the agent list are panels of the right
 * column now, so an older payload names three tabs the center no longer has.
 * A layout is restored only when its panel set is exact, which those are not,
 * so they are left where they are and the center opens from its defaults.
 */
export const CENTER_LAYOUT_KEY = 'mac-command-bar.next.center-layout-v6';

export function loadLayout<T>(storage: LayoutStorage, key: string): T | null {
  try {
    const raw = storage.getItem(key);
    if (!raw) return null;
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

/** Quota-safe write. Returns false instead of throwing (layout loss is benign). */
export function saveLayout(storage: LayoutStorage, key: string, layout: unknown): boolean {
  try {
    storage.setItem(key, JSON.stringify(layout));
    return true;
  } catch {
    return false;
  }
}

export function clearLayout(storage: LayoutStorage, key: string): void {
  try {
    storage.removeItem(key);
  } catch {
    // A storage that cannot even remove is a storage we ignore.
  }
}

/**
 * Panel ids inside a `SerializedGridviewComponent` (dockview-core 6.6.1 shape:
 * `{ grid: { root: <branch|leaf tree> } }`, leaves carry `data.id`). Malformed
 * input yields an empty set — the caller then rebuilds from defaults.
 */
export function gridPanelIds(serialized: unknown): Set<string> {
  const ids = new Set<string>();
  const root = (serialized as { grid?: { root?: unknown } } | null)?.grid?.root;
  const walk = (node: unknown): void => {
    if (!node || typeof node !== 'object') return;
    const { type, data } = node as { type?: string; data?: unknown };
    if (type === 'branch' && Array.isArray(data)) {
      for (const child of data) walk(child);
      return;
    }
    if (type === 'leaf' && data && typeof data === 'object') {
      const id = (data as { id?: unknown }).id;
      if (typeof id === 'string') ids.add(id);
    }
  };
  walk(root);
  return ids;
}

/**
 * The width a top-level column was saved at, or null when the layout has no
 * such column or no number for it. dockview restores every column scaled by
 * the new grid width over the saved one, so this saved number, not what the
 * restore produced, is the width the user left the column at.
 */
export function storedColumnWidth(serialized: unknown, id: string): number | null {
  const columns = (serialized as { grid?: { root?: { data?: unknown } } } | null)?.grid?.root?.data;
  if (!Array.isArray(columns)) return null;
  const column = columns.find(
    (node) => node?.type === 'leaf' && (node.data as { id?: unknown } | undefined)?.id === id
  ) as { size?: unknown } | undefined;
  return typeof column?.size === 'number' ? column.size : null;
}

/**
 * Pane ids inside a `SerializedPaneview` (dockview-core 6.6.1 shape:
 * `{ size, views: [{ size, expanded?, data: { id, component, title, … } }] }`).
 * Collapsed panes are in `views` exactly like open ones, so this returns the
 * whole stack either way. Malformed input yields an empty set — the caller then
 * rebuilds from defaults.
 */
export function paneviewPanelIds(serialized: unknown): Set<string> {
  const ids = new Set<string>();
  const views = (serialized as { views?: unknown } | null)?.views;
  if (!Array.isArray(views)) return ids;
  for (const view of views) {
    if (!view || typeof view !== 'object') continue;
    const data = (view as { data?: unknown }).data;
    if (!data || typeof data !== 'object') continue;
    const id = (data as { id?: unknown }).id;
    if (typeof id === 'string') ids.add(id);
  }
  return ids;
}

/** True when `ids` is EXACTLY `expected` (both directions, order-free). */
export function panelSetMatches(ids: Iterable<string>, expected: Iterable<string>): boolean {
  const a = new Set(ids);
  const b = new Set(expected);
  if (a.size !== b.size) return false;
  for (const id of a) if (!b.has(id)) return false;
  return true;
}
