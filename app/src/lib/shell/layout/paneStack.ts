/**
 * paneStack.ts — the /next left rail's stack of collapsible sections (one
 * dockview Paneview). DOM-only: zero backend IO, zero Svelte imports.
 *
 * Same teleport contract as `frame.ts`: every section's
 * body is a Svelte-owned element that this module MOVES into a dockview-owned
 * host div, and hands back to its original parent ("parking") whenever the pane
 * that held it dies while the shell lives on. dockview never renders or
 * disposes app DOM.
 */
import { createPaneview, type IPanePart, type PaneviewApi } from 'dockview-core';

// The sibling import carries its `.ts` extension because
// `scripts/paneLayout.test.mjs` loads this module through node's own resolver,
// which does not fill an extension in the way the bundler does.
import {
  loadLayout,
  paneviewPanelIds,
  panelSetMatches,
  saveLayout,
  type LayoutStorage
} from './layoutStorage.ts';
import { viewPanesKey, type SidebarViewId } from './sidebarViews.ts';

export interface PaneSpec {
  id: string;
  /** Label drawn in dockview's built-in pane header. */
  title: string;
  /** Svelte-owned element, teleported into the pane body. */
  element: HTMLElement;
  /**
   * Initial height of the whole pane in px — header row included — used only
   * when there is no stored layout. dockview measures panes, not bodies, so a
   * collapsed pane sits at its header height whatever this says.
   */
  size: number;
  /** Start open? (default true) */
  expanded?: boolean;
  /** Optional body-size limits supplied by the side-pane registry. */
  minimumSize?: number;
  maximumSize?: number | null;
  /** Body height needed by the current content; used to cap empty slack. */
  contentSize?: number | (() => number);
  /** Whether this pane is included in the persisted roster. */
  persistent?: boolean;
}

export interface PaneStackOptions {
  /** The injected layout authority. It is never created by this module. */
  layoutStore?: LayoutStorage;
  /** Stable entry inside the side-pane v1 map. */
  layoutId?: string;
  panes: PaneSpec[];
  onLayoutPersisted?: (ok: boolean) => void;
}

export interface PaneStack {
  api: PaneviewApi;
  resetLayout(): void;
  layout(width: number, height: number): void;
  fitContent(): void;
  dispose(): void;
}

const COMPONENT = 'pane';

/** The one registry-generated key family for new side-pane layouts. */
export const SIDE_PANE_LAYOUT_KEY = 'mac-command-bar.next.side-panes-v1';
export const SIDE_PANE_LAYOUT_VERSION = 1 as const;

type SidePaneLayoutMap = {
  version: typeof SIDE_PANE_LAYOUT_VERSION;
  layouts: Record<string, object>;
};

const NO_LAYOUT_STORAGE: LayoutStorage = {
  getItem: () => null,
  setItem: () => {},
  removeItem: () => {}
};

/** Old sidebar view ids whose one-pane layouts can be carried forward. */
const LEGACY_VIEW_FOR_PANE: Readonly<Record<string, SidebarViewId>> = {
  files: 'explorer',
  'source-control': 'source-control',
  worktrees: 'worktrees',
  run: 'stacks',
  'run-configurations': 'stacks',
  context: 'context',
  problems: 'problems'
};

function objectRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function readSidePaneMap(storage: LayoutStorage): SidePaneLayoutMap {
  const raw = loadLayout<unknown>(storage, SIDE_PANE_LAYOUT_KEY);
  const record = objectRecord(raw);
  if (!record) return { version: SIDE_PANE_LAYOUT_VERSION, layouts: {} };

  const layouts = objectRecord(record.layouts);
  if (record.version === SIDE_PANE_LAYOUT_VERSION && layouts) {
    return { version: SIDE_PANE_LAYOUT_VERSION, layouts: { ...layouts } as Record<string, object> };
  }

  // Be tolerant of an early v1 draft that stored the entry map directly. It is
  // still the same key family and can be normalized on the next write.
  const directLayouts = Object.fromEntries(
    Object.entries(record).filter(([, value]) => objectRecord(value) !== null)
  ) as Record<string, object>;
  return { version: SIDE_PANE_LAYOUT_VERSION, layouts: directLayouts };
}

function writeSidePaneMap(storage: LayoutStorage, map: SidePaneLayoutMap): boolean {
  return saveLayout(storage, SIDE_PANE_LAYOUT_KEY, map);
}

function defaultLayoutId(options: PaneStackOptions): string {
  return (
    options.layoutId?.trim() ||
    `panes:${options.panes
      .map((pane) => pane.id)
      .slice()
      .sort()
      .join(',')}`
  );
}

/** Rewrite a valid one-pane legacy payload without changing its size/state. */
export function migrateLegacyPaneLayout(
  stored: unknown,
  sourcePaneId: string,
  targetPaneId: string
): object | null {
  if (!canRestorePaneLayout(stored, [sourcePaneId])) return null;
  const value = stored as { views: unknown[] };
  const views = value.views.map((view) => {
    if (!view || typeof view !== 'object') return view;
    const next = { ...(view as Record<string, unknown>) };
    const data = objectRecord(next.data);
    if (data) {
      const params = objectRecord(data.params);
      next.data = {
        ...data,
        id: targetPaneId,
        ...(params ? { params: { ...params, paneId: targetPaneId } } : {})
      };
    }
    return next;
  });
  const migrated = { ...(value as Record<string, unknown>), views };
  return canRestorePaneLayout(migrated, [targetPaneId]) ? migrated : null;
}

/**
 * Read a valid legacy `viewPanesKey(id)` payload for a new pane id. Invalid or
 * incomplete payloads are ignored; callers then build the registered defaults.
 */
export function readLegacyViewPaneLayout(
  storage: LayoutStorage,
  targetPaneIds: Iterable<string>
): object | null {
  const ids = [...new Set(targetPaneIds)];
  if (ids.length !== 1) return null;
  const targetId = ids[0];
  const legacyView = LEGACY_VIEW_FOR_PANE[targetId];
  if (!legacyView) return null;
  const stored = loadLayout<unknown>(storage, viewPanesKey(legacyView));
  return migrateLegacyPaneLayout(stored, targetId === 'run' ? 'run-configurations' : targetId, targetId);
}

/**
 * Combine the valid per-view legacy layouts into one exact side-pane payload.
 * It returns null unless every requested pane has a valid source layout, so a
 * partial migration can never produce a stack missing a registered pane.
 */
export function migrateViewPaneLayouts(
  storage: LayoutStorage,
  targetPaneIds: Iterable<string>
): object | null {
  const ids = [...new Set(targetPaneIds)];
  if (ids.length === 0) return null;
  const views: unknown[] = [];
  let size = 0;
  for (const targetId of ids) {
    const legacyView = LEGACY_VIEW_FOR_PANE[targetId];
    if (!legacyView) return null;
    const stored = loadLayout<unknown>(storage, viewPanesKey(legacyView));
    const sourceId = targetId === 'run' ? 'run-configurations' : targetId;
    const migrated = migrateLegacyPaneLayout(stored, sourceId, targetId);
    if (!migrated) return null;
    const payload = migrated as { size?: unknown; views?: unknown };
    if (typeof payload.size === 'number' && Number.isFinite(payload.size)) size += payload.size;
    if (!Array.isArray(payload.views)) return null;
    views.push(...payload.views);
  }
  const combined = { size, views };
  return canRestorePaneLayout(combined, ids) ? combined : null;
}

export const migrateViewPanesLayouts = migrateViewPaneLayouts;

/**
 * Is this stored layout safe to restore into a stack of exactly `paneIds`?
 *
 * Exported so it can be tested without a DOM. The `views` array check is not
 * belt-and-braces: `PaneviewComponent.fromJSON` disposes the live paneview
 * BEFORE it reads `views`, so handing it a layout whose `views` is missing or
 * is an object throws with the component already torn down and nothing left to
 * rebuild into. Everything unusable has to be rejected before the call.
 */
export function canRestorePaneLayout(stored: unknown, paneIds: Iterable<string>): boolean {
  if (!stored || typeof stored !== 'object') return false;
  if (!Array.isArray((stored as { views?: unknown }).views)) return false;
  return panelSetMatches(paneviewPanelIds(stored), paneIds);
}

/** Resolve the body maximum that a live content measurement should apply. */
export function resolvePaneBodyMaximum(
  minimumBodySize: number,
  requestedContentSize: number | undefined,
  currentMaximumBodySize: number
): number {
  const requested = Number.isFinite(requestedContentSize)
    ? requestedContentSize as number
    : currentMaximumBodySize;
  return Number.isFinite(requested)
    ? Math.max(minimumBodySize, requested)
    : Number.POSITIVE_INFINITY;
}

export function createPaneStack(container: HTMLElement, options: PaneStackOptions): PaneStack {
  const specs = new Map(options.panes.map((pane) => [pane.id, pane]));
  if (specs.size !== options.panes.length) {
    throw new TypeError('PaneStack panes must have unique ids');
  }
  const layoutStorage = options.layoutStore ?? NO_LAYOUT_STORAGE;
  const centralLayoutId = defaultLayoutId(options);

  /** One persistence adapter, backed by the injected store and one v1 map entry. */
  const loadPersistedLayout = (): object | null => {
    const map = readSidePaneMap(layoutStorage);
    const stored = map.layouts[centralLayoutId];
    if (canRestorePaneLayout(stored, specs.keys())) return stored;

    // A valid one-pane view layout is migrated exactly once into the v1 map.
    // A mismatch is deliberately not copied: the next launch must rebuild the
    // exact registered roster rather than resurrecting an obsolete pane set.
    const migrated = migrateViewPaneLayouts(layoutStorage, specs.keys());
    if (!migrated) return null;
    const next = { ...map.layouts, [centralLayoutId]: migrated };
    writeSidePaneMap(layoutStorage, {
      version: SIDE_PANE_LAYOUT_VERSION,
      layouts: next
    });
    return migrated;
  };

  const clearPersistedLayout = (): void => {
    const map = readSidePaneMap(layoutStorage);
    if (!(centralLayoutId in map.layouts)) return;
    const layouts = { ...map.layouts };
    delete layouts[centralLayoutId];
    writeSidePaneMap(layoutStorage, { version: SIDE_PANE_LAYOUT_VERSION, layouts });
  };

  const savePersistedLayout = (layout: object): boolean => {
    const map = readSidePaneMap(layoutStorage);
    return writeSidePaneMap(layoutStorage, {
      version: SIDE_PANE_LAYOUT_VERSION,
      layouts: { ...map.layouts, [centralLayoutId]: layout }
    });
  };

  /** Where each body element goes back to when its pane dies. */
  const parking = new Map(
    options.panes.map((pane) => [pane.id, pane.element.parentElement as HTMLElement | null])
  );
  let synchronizingDepth = 0;
  let disposed = false;

  /**
   * A Paneview body part is a plain object, not a subclass: `createComponent`
   * returns an `IPanePart` and dockview wraps it in its own `PaneFramework`
   * panel (unlike the Gridview in `frame.ts`, which wants a `GridviewPanel`).
   */
  const createBodyPart = (): IPanePart => {
    const element = document.createElement('div');
    element.className = 'pane-body-host';
    let paneId: string | null = null;
    return {
      element,
      init(parameters): void {
        const candidate = parameters.params?.paneId;
        if (typeof candidate !== 'string' || !specs.has(candidate)) return;
        paneId = candidate;
        const content = specs.get(candidate)!.element;
        if (content.parentElement !== element) element.replaceChildren(content);
      },
      update(): void {},
      dispose(): void {
        if (disposed || !paneId) return;
        // Pane gone while the shell lives on: hand the body back to its
        // Svelte-owned parking node so nothing app-owned dies with dockview.
        const content = specs.get(paneId)?.element;
        const home = parking.get(paneId) ?? null;
        if (content && home && content.parentElement !== home) home.appendChild(content);
      }
    };
  };

  const api = createPaneview(container, {
    className: 'shell-pane-stack',
    createComponent: createBodyPart
  });

  const addPaneFor = (pane: PaneSpec): void => {
    const contentSize = typeof pane.contentSize === 'function' ? pane.contentSize() : pane.contentSize;
    api.addPanel({
      id: pane.id,
      component: COMPONENT,
      title: pane.title,
      params: { paneId: pane.id },
      size: pane.size,
      minimumBodySize: pane.minimumSize,
      maximumBodySize: contentSize ?? pane.maximumSize ?? undefined,
      // The add option is spelled `isExpanded`; the SERIALIZED field is
      // `expanded`. Setting it here is enough — a pane built collapsed reports
      // a maximum size of just its header, so there is no second step and no
      // collapse animation on start-up.
      isExpanded: pane.expanded !== false
    });
  };

  const buildDefault = (): void => {
    for (const pane of options.panes) addPaneFor(pane);
  };

  /**
   * Keep a small pane from inheriting all of the stack's spare height. A
   * stored layout can be wildly out of date after grouping or filtering, so
   * every content-sized pane gets a live maximum and a fair share when the
   * requested content is taller than the available stack.
   */
  const fitContent = (): void => {
    if (disposed || api.height <= 0) return;
    const expanded = api.panels.filter((panel) => panel.isExpanded());
    if (expanded.length === 0) return;

    const collapsedHeight = api.panels
      .filter((panel) => !panel.isExpanded())
      .reduce((total, panel) => total + panel.minimumSize, 0);
    const panes = expanded.map((panel) => {
      const spec = specs.get(panel.id);
      const requested = typeof spec?.contentSize === 'function' ? spec.contentSize() : spec?.contentSize;
      const bodySize = resolvePaneBodyMaximum(panel.minimumBodySize, requested, panel.maximumBodySize);
      const headerSize = Math.max(0, panel.minimumSize - panel.minimumBodySize);
      const desiredSize = headerSize + bodySize;
      // dockview's pane API emits whole-pane constraints but does not wire the
      // event back to PaneviewPanel.maximumBodySize. Set the supported body
      // maximum directly, then lay out the stack against the new limits.
      (panel as unknown as { maximumBodySize: number }).maximumBodySize = bodySize;
      return {
        panel,
        minimumSize: panel.minimumSize,
        desiredSize
      };
    });

    const available = Math.max(0, api.height - collapsedHeight);
    const minimumTotal = panes.reduce((total, pane) => total + pane.minimumSize, 0);
    const desiredTotal = panes.reduce((total, pane) => total + pane.desiredSize, 0);
    const sizes = panes.map((pane) => pane.desiredSize);

    if (desiredTotal > available) {
      sizes.splice(0, sizes.length, ...panes.map((pane) => pane.minimumSize));
      let remaining = Math.max(0, available - minimumTotal);
      const capacity = panes.map((pane) => Math.max(0, pane.desiredSize - pane.minimumSize));
      while (remaining > 0) {
        const open = capacity.reduce((count, amount) => count + (amount > 0 ? 1 : 0), 0);
        if (open === 0) break;
        const share = remaining / open;
        let used = 0;
        for (let index = 0; index < capacity.length; index += 1) {
          if (capacity[index] <= 0) continue;
          const amount = Math.min(capacity[index], share);
          sizes[index] += amount;
          capacity[index] -= amount;
          used += amount;
        }
        if (used <= 0) break;
        remaining -= used;
      }
    }

    api.layout(api.width, api.height);
    panes.forEach(({ panel }, index) => {
      panel.api.setSize({ size: Math.round(sizes[index]) });
    });
  };

  /**
   * Empty the stack WITHOUT `api.clear()`.
   *
   * `PaneviewComponent.clear()` disposes its inner `Paneview`, and that dispose
   * detaches the container div the panes live in — but nothing ever builds a
   * replacement, so every pane added afterwards renders into a node that is no
   * longer on the page. (`fromJSON` gets away with calling it because it then
   * constructs a fresh `Paneview` itself; a plain clear-then-rebuild does not.)
   * Removing panes one at a time leaves the component intact and fires each
   * body part's `dispose`, which is what returns the bodies to parking.
   */
  const removeAllPanes = (): void => {
    for (const panel of [...api.panels]) {
      try {
        api.removePanel(panel);
      } catch {
        // A pane that refuses to go is left in place; the rebuild below skips
        // nothing else, and "Reset layout" can be pressed again.
      }
    }
  };

  /**
   * Run a programmatic layout mutation with persistence suppressed.
   *
   * The owner forbids production frontend schedulers, so the guard is scoped to
   * the synchronous mutation only. Any deferred dockview event that follows is
   * treated as ordinary layout state and may persist.
   */
  const runSynchronized = (fn: () => void): void => {
    synchronizingDepth += 1;
    try {
      fn();
    } finally {
      if (synchronizingDepth > 0) synchronizingDepth -= 1;
    }
  };

  /**
   * Size the stack to its container BEFORE anything is restored or built, for
   * the same reason as the grid in `frame.ts` — and here it is load-bearing
   * twice over: `fromJSON` reads the component's current width and height and
   * lays the restored panes out at exactly those numbers, so restoring into a
   * 0 x 0 stack squashes every pane to nothing.
   *
   * A container with no size yet is skipped rather than forced: dockview's own
   * resize watcher delivers the first real layout, and `persistSoon` refuses to
   * write a stack that has never had a real size.
   */
  const layoutToContainer = (): void => {
    const width = container.clientWidth;
    const height = container.clientHeight;
    if (width > 0 && height > 0) api.layout(width, height);
  };

  layoutToContainer();

  runSynchronized(() => {
    const stored = loadPersistedLayout();
    if (canRestorePaneLayout(stored, specs.keys())) {
      try {
        api.fromJSON(stored as never);
        return;
      } catch {
        // Half-restored: drop the layout that did this so the next launch
        // starts clean, then rebuild what we can here and now.
        clearPersistedLayout();
        removeAllPanes();
      }
    }
    buildDefault();
  });
  fitContent();

  const persistSoon = (): void => {
    if (synchronizingDepth > 0 || disposed) return;
    // Never store a stack measured at zero: the pane sizes in it are
    // meaningless and the next launch would restore from them.
    if (api.width <= 0 || api.height <= 0) return;
    let ok = false;
    try {
      ok = savePersistedLayout(api.toJSON());
    } catch {
      ok = false;
    }
    options.onLayoutPersisted?.(ok);
  };

  /**
   * One listener covers sizes AND open/closed. Opening or closing a pane calls
   * `PaneviewPanel.setExpanded`, which fires its expansion event, which the
   * `Paneview` forwards as a layout change — so `onDidLayoutChange` already
   * carries every collapse, and `toJSON` writes each pane's `expanded` flag.
   * Subscribing to the panes' own expansion events as well would only persist
   * the same thing twice.
   */
  const changeListener = api.onDidLayoutChange(persistSoon);

  return {
    api,
    resetLayout(): void {
      clearPersistedLayout();
      runSynchronized(() => {
        removeAllPanes();
        buildDefault();
      });
      persistSoon();
    },
    layout(width: number, height: number): void {
      api.layout(width, height);
      fitContent();
    },
    fitContent(): void {
      fitContent();
    },
    dispose(): void {
      disposed = true; // body-part dispose() no-ops: page teardown owns the DOM now
      changeListener.dispose();
      api.dispose();
    }
  };
}
