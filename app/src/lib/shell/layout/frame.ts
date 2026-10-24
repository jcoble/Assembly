/**
 * frame.ts — the /next shell's Gridview root. Left to right: the sessions
 * column, the center (the chat), and the tab pane (tools = tab pane, present
 * only while the top row has tabs); the bottom dock sits under the center only. It owns only DOM and Dockview state; persistence is supplied by
 * the shell.
 *
 * Teleport contract: every region's content is a Svelte-owned element that
 * this module MOVES into a dockview-owned host div. dockview never renders or
 * disposes app DOM; disposing the frame moves nothing (Svelte still owns the
 * nodes and the page is unmounting anyway).
 */
import {
  createGridview,
  GridviewPanel,
  Orientation,
  type GridPanelViewState,
  type GridviewApi,
  type IFrameworkPart,
  type SerializedGridObject,
  type SerializedGridviewComponent
} from 'dockview-core';

import { gridPanelIds, storedColumnWidth } from './layoutStorage';

export type ShellRegionId = 'sessions' | 'center' | 'tools' | 'dock';

const REGION_IDS: readonly ShellRegionId[] = ['sessions', 'center', 'tools', 'dock'];

/**
 * The regions a stored layout must carry to be worth restoring.
 *
 * The dock and the tab pane are absent from this list because each is absent
 * from the grid much of the time — the dock whenever the Problems list is not
 * at the bottom, the pane whenever no top tab is open. Demanding them would
 * throw such a layout away and rebuild the default on every launch, losing the
 * column widths.
 */
const REQUIRED_REGION_IDS: readonly ShellRegionId[] = ['sessions', 'center'];

/** Every required region is there, and nothing this frame does not own is. */
function storedRegionsUsable(ids: Iterable<string>): boolean {
  const present = new Set(ids);
  const known: readonly string[] = REGION_IDS;
  return (
    REQUIRED_REGION_IDS.every((id) => present.has(id)) &&
    [...present].every((id) => known.includes(id))
  );
}
const COMPONENT = 'shell-region';

/** What the two side columns open at, in px, before anyone drags a divider.
 * The sessions column opens so its card is 320px wide, the approved mockup's
 * rail: the region is the card plus its 3px gutter on each side. The tab pane
 * opens wide enough for a diff or an editor beside the chat. */
export const SESSIONS_WIDTH = 326;
export const TOOLS_WIDTH = 720;

/**
 * How narrow and how wide the tool column may be dragged. It stops short of
 * the window's edge because the center pane keeps `CENTER_MIN_WIDTH` whatever
 * else happens. Wider than this is the whole right region widened over the
 * center (`setToolsExpanded`), not a dragged seam.
 */
export const TOOLS_MIN_WIDTH = 360;
export const TOOLS_MAX_WIDTH = 1600;

/** What the middle keeps. A conversation narrower than this is unreadable. */
export const CENTER_MIN_WIDTH = 420;

/** How narrow and how wide the sessions column may be dragged while it is
 * open. Exported because the page puts these back when the column is unfolded
 * — folding it replaces them with a fixed width. */
export const SESSIONS_MIN_WIDTH = 220;
export const SESSIONS_MAX_WIDTH = 560;

/** What the bottom strip opens at. */
export const DOCK_HEIGHT = 180;

/** The width of the sessions column folded up: one icon-sized cell per
 * session and nothing else. Fixed the same way the surface rail is, so the
 * divider beside a folded column cannot be dragged. */
export const SESSIONS_STRIP_WIDTH = 52;

export interface ShellFrameOptions {
  readLayout: () => Promise<unknown>;
  writeLayout: (layout: unknown) => Promise<void>;
  regions: Record<ShellRegionId, HTMLElement>;
  onLayoutPersisted?: (ok: boolean) => void;
}

/** How narrow and how wide a region may be dragged. Both optional: leaving one
 * out keeps whatever limit the region already has. */
export interface RegionWidthLimits {
  minimumWidth?: number;
  maximumWidth?: number;
}

/** How short and how tall a region may be dragged. The dock is the only region
 * laid out vertically, and this is what lets it be closed entirely. */
export interface RegionHeightLimits {
  minimumHeight?: number;
  maximumHeight?: number;
}

export interface ShellFrame {
  api: GridviewApi;
  /** Resolves after the SQLite-backed layout has been read, or ignored. */
  ready: Promise<void>;
  resetLayout(): void;
  /**
   * Give one region a width, optionally changing what it may be dragged to.
   *
   * This is how a column asks to be folded up or opened out: the width a
   * region takes is the grid's business, not the column's, so nothing hides
   * content behind a CSS width of its own. The limits are applied first —
   * asking for 52px while the region's own minimum is still 220 would simply
   * be clamped back to 220.
   */
  setRegionWidth(id: ShellRegionId, width: number, limits?: RegionWidthLimits): void;
  /**
   * Give one region a height, optionally changing what it may be dragged to.
   * The mirror of `setRegionWidth`, for the bottom dock — the one region this
   * frame lays out vertically. Closing the dock means asking for a height of
   * zero, which a minimum height would otherwise clamp back up, so the limits
   * are applied first here for the same reason they are there.
   */
  setRegionHeight(id: ShellRegionId, height: number, limits?: RegionHeightLimits): void;
  /**
   * Put the bottom dock in the grid, or take it out.
   *
   * Closing the dock is not the same as making it short. A region of zero
   * height is still a region: the grid keeps its divider, and the middle
   * column keeps the room the divider and the empty view sit in — measured at
   * 16px above the status bar with the strip shut. Removing the region is what
   * gives that room back, and adding it again is what a reopened strip means.
   *
   * The dock's content is Svelte-owned and only borrowed by the grid, so it
   * survives the round trip: removal detaches it, and the next add moves it
   * back into the new host.
   */
  setDockPresent(present: boolean): void;
  /** Put the right tools region in the grid or remove it completely. */
  setToolsPresent(present: boolean): void;
  /**
   * Widen the right region over the center, or give it back its own width.
   * The center is locked at no width rather than removed, so its surfaces stay
   * mounted. Saves made while widened record the collapsed layout, so a
   * restart always opens collapsed at the normal width.
   */
  setToolsExpanded(expanded: boolean): void;
  /**
   * Say what a region may be dragged to, without touching the width it has.
   *
   * A stored layout carries each region's limits as well as its width, so a
   * layout written while a column was folded restores the column locked at
   * strip width — minimum and maximum both 52px, which is also a divider that
   * cannot be dragged. That is right while the column is meant to be folded and
   * wrong the moment it is not, and the two facts are stored separately and can
   * disagree. This is how the page says "open, and draggable again" on restore
   * while leaving a width the user chose alone.
   */
  setRegionLimits(id: ShellRegionId, limits: RegionWidthLimits): void;
  /** How wide a region is right now, or null if there is no such region. Used
   * to tell a width the user dragged from one restored below its own minimum. */
  regionWidth(id: ShellRegionId): number | null;
  layout(width: number, height: number): void;
  dispose(): void;
}

/** Gridview panel that adopts a Svelte-owned element into `this.element`. */
class TeleportGridPanel extends GridviewPanel {
  private readonly adopt: (id: string, host: HTMLElement) => void;

  constructor(id: string, component: string, adopt: (id: string, host: HTMLElement) => void) {
    super(id, component);
    this.adopt = adopt;
  }

  protected getComponent(): IFrameworkPart {
    this.adopt(this.id, this.element);
    return {
      update: () => {},
      dispose: () => {}
    };
  }
}

export function createShellFrame(container: HTMLElement, options: ShellFrameOptions): ShellFrame {
  let synchronizingDepth = 0;
  let disposed = false;
  let layoutVersion = 0;
  let rememberedToolsWidth = TOOLS_WIDTH;
  let toolsTransitionVersion = 0;
  let toolsFrame = 0;
  /** True only once a widening has actually been applied to the grid. */
  let toolsExpanded = false;
  let collapsedToolsWidth = TOOLS_WIDTH;

  const adopt = (id: string, host: HTMLElement): void => {
    const region = options.regions[id as ShellRegionId];
    if (!region) return;
    host.classList.add('shell-region-host', `shell-region-host-${id}`);
    if (region.parentElement !== host) host.replaceChildren(region);
  };

  const api = createGridview(container, {
    orientation: Orientation.HORIZONTAL,
    proportionalLayout: true,
    hideBorders: true,
    className: 'shell-grid',
    createComponent: ({ id, name }) => new TeleportGridPanel(id, name, adopt)
  });

  /** Ask one region for a width, and for what it may be dragged to. A region
   * that will not take it keeps the width it has: a default layout a few pixels
   * off is not worth failing a launch. */
  const setRegionWidth = (
    id: ShellRegionId,
    width: number,
    limits?: RegionWidthLimits
  ): void => {
    try {
      const panel = api.getPanel(id);
      if (!panel) return;
      if (limits) panel.api.setConstraints(limits);
      panel.api.setSize({ width });
    } catch {
      // nothing to do — the arrangement is still usable
    }
  };

  /** Ask one region for a height, and for what it may be dragged to. Same
   * tolerance as `setRegionWidth`. */
  const setRegionHeight = (
    id: ShellRegionId,
    height: number,
    limits?: RegionHeightLimits
  ): void => {
    try {
      const panel = api.getPanel(id);
      if (!panel) return;
      if (limits) panel.api.setConstraints(limits);
      panel.api.setSize({ height });
    } catch {
      // nothing to do — the arrangement is still usable
    }
  };

  /** Say what a region may be dragged to and nothing else. Same tolerance as
   * `setRegionWidth`: a region that will not take it keeps what it has. */
  const setRegionLimits = (id: ShellRegionId, limits: RegionWidthLimits): void => {
    try {
      api.getPanel(id)?.api.setConstraints(limits);
    } catch {
      // nothing to do — the arrangement is still usable
    }
  };

  /** How wide a region is right now, or null if it is not there. */
  const regionWidth = (id: ShellRegionId): number | null => {
    try {
      return api.getPanel(id)?.api.width ?? null;
    } catch {
      return null;
    }
  };

  /**
   * Below CENTER only: the dock spans the middle column, not the side columns.
   *
   * No minimum height, on purpose. The Problems list can be moved to the tool
   * column or hidden altogether, and both answers close this strip completely —
   * a minimum of 96px would clamp that back to a 96px strip of nothing.
   */
  const addDock = (): void => {
    api.addPanel({
      id: 'dock',
      component: COMPONENT,
      position: { direction: 'below', referencePanel: 'center' },
      size: DOCK_HEIGHT,
      minimumHeight: 0
    });
  };

  const addTools = (): void => {
    api.addPanel({
      id: 'tools',
      component: COMPONENT,
      position: { direction: 'right', referencePanel: 'center' },
      size: rememberedToolsWidth,
      minimumWidth: TOOLS_MIN_WIDTH,
      maximumWidth: TOOLS_MAX_WIDTH
    });
  };

  /** The default arrangement; also the fallback whenever restore is unusable. */
  const buildDefault = (): void => {
    api.addPanel({ id: 'center', component: COMPONENT, minimumWidth: CENTER_MIN_WIDTH });
    // The left side belongs to the sessions list and nothing else.
    api.addPanel({
      id: 'sessions',
      component: COMPONENT,
      position: { direction: 'left', referencePanel: 'center' },
      size: SESSIONS_WIDTH,
      minimumWidth: SESSIONS_MIN_WIDTH,
      maximumWidth: SESSIONS_MAX_WIDTH
    });
    // The tab pane is not part of the default: only `setToolsPresent(true)`
    // adds it, when the top row gets its first tab.
    addDock();
    // Say the sessions width again, now that every region exists.
    //
    // The dock above is what makes this necessary: putting it under the middle
    // turns the middle from one region into a column of two, and dockview does
    // that by lifting the middle out of the row and putting it back. The width
    // it gave up goes round the other regions on the way out and comes back
    // unevenly. Asking for the width once the arrangement is finished settles
    // it where it was meant to be.
    setRegionWidth('sessions', SESSIONS_WIDTH);
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
   * Size the grid to its container BEFORE anything is restored or built.
   * dockview's constructor starts the grid at 0 x 0, and a restore into a
   * zero-width grid clamps every region to its minimum and remembers those
   * squashed proportions — which the first real layout then scales up wrong.
   * (This mirrors the old shell's `layoutDockviewApiToContainer`.)
   *
   * A container with no size yet — the route is hidden, say — is skipped rather
   * than forced: the first real layout does the job, and `persistSoon` refuses
   * to write a grid that has never had a real size.
   */
  const layoutToContainer = (): void => {
    const width = container.clientWidth;
    const height = container.clientHeight;
    if (width > 0 && height > 0) api.layout(width, height);
  };

  layoutToContainer();

  runSynchronized(buildDefault);

  async function persistLayout(layout: unknown): Promise<void> {
    try {
      await options.writeLayout(layout);
      if (!disposed) options.onLayoutPersisted?.(true);
    } catch {
      if (!disposed) options.onLayoutPersisted?.(false);
    }
  }

  /**
   * The layout a save records while the right region is widened: the right
   * column back at its own width and limits, the center column taking back the
   * difference with its own limits. Everything else is the live snapshot's.
   * Columns are the root's children, sized by width; the center may sit in a
   * column branch with the dock under it.
   */
  const collapsedLayout = (layout: SerializedGridviewComponent): SerializedGridviewComponent => {
    type GridNode = SerializedGridObject<GridPanelViewState>;
    const leaf = (node: GridNode, id: string): GridNode | undefined =>
      node.type === 'leaf'
        ? (node.data as GridPanelViewState).id === id ? node : undefined
        : (node.data as GridNode[]).map((child) => leaf(child, id)).find(Boolean);
    const columns = layout.grid.root.data as GridNode[];
    const toolsColumn = columns.find((column) => leaf(column, 'tools'));
    const centerColumn = columns.find((column) => leaf(column, 'center'));
    const centerLeaf = centerColumn && leaf(centerColumn, 'center');
    if (!toolsColumn || !centerColumn || !centerLeaf) return layout;
    centerColumn.size = (centerColumn.size ?? 0) + (toolsColumn.size ?? 0) - collapsedToolsWidth;
    toolsColumn.size = collapsedToolsWidth;
    toolsColumn.data = { ...(toolsColumn.data as GridPanelViewState), maximumWidth: TOOLS_MAX_WIDTH };
    centerLeaf.data = {
      ...(centerLeaf.data as GridPanelViewState),
      minimumWidth: CENTER_MIN_WIDTH,
      maximumWidth: undefined
    };
    return layout;
  };

  const persistSoon = (): void => {
    if (synchronizingDepth > 0 || disposed) return;
    // Never store a grid measured at zero: every region in it sits at its
    // minimum, and the next launch scales those wrong sizes up to the window.
    if (api.width <= 0 || api.height <= 0) return;
    try {
      void persistLayout(toolsExpanded ? collapsedLayout(api.toJSON()) : api.toJSON());
    } catch {
      options.onLayoutPersisted?.(false);
    }
  };

  const changeListener = api.onDidLayoutChange(() => {
    if (disposed || synchronizingDepth > 0) return;
    layoutVersion += 1;
    persistSoon();
  });

  const restoreVersion = layoutVersion;
  const ready = restoreSavedLayout(restoreVersion);

  async function restoreSavedLayout(expectedVersion: number): Promise<void> {
    try {
      const stored = await options.readLayout();
      if (disposed || layoutVersion !== expectedVersion) return;
      if (!stored || !storedRegionsUsable(gridPanelIds(stored))) return;
      runSynchronized(() => {
        if (disposed || layoutVersion !== expectedVersion) return;
        try {
          api.fromJSON(stored as never);
          // fromJSON scales every column by today's grid width over the saved
          // one, and applies the saved limits only after that. A layout saved
          // in a narrower grid therefore came back with a wider sessions
          // column, which was saved and scaled again on the next launch. Give
          // the column back the width it was saved at, never above its
          // maximum. A width below the minimum is a folded column; the page
          // decides on that once the frame is ready.
          const sessions = storedColumnWidth(stored, 'sessions');
          if (sessions !== null) setRegionWidth('sessions', Math.min(sessions, SESSIONS_MAX_WIDTH));
        } catch {
          try {
            api.clear();
            buildDefault();
          } catch {
            // Keep whatever usable portion of the existing arrangement remains.
          }
        }
      });
    } catch {
      // Invalid saved layout leaves the default frame in place.
    }
  }

  const setDockPresent = (present: boolean): void => {
    try {
      const panel = api.getPanel('dock');
      if (present === Boolean(panel)) return;
      // Both side widths are read before the change and asked for again after,
      // for the reason `buildDefault` gives: adding the dock lifts the middle
      // out of the row and puts it back as a column, and the width it gives up
      // comes back unevenly. Removing it does the same in reverse.
      const sessions = regionWidth('sessions');
      const tools = regionWidth('tools');
      runSynchronized(() => {
        if (present) addDock();
        else if (panel) api.removePanel(panel);
        if (sessions !== null) setRegionWidth('sessions', sessions);
        if (tools !== null) setRegionWidth('tools', tools);
      });
      persistSoon();
    } catch {
      // nothing to do — the arrangement is still usable
    }
  };

  const setToolsPresent = (present: boolean): void => {
    const transitionVersion = ++toolsTransitionVersion;
    try {
      if (present === Boolean(api.getPanel('tools'))) {
        options.regions.center.style.visibility = '';
        return;
      }
    } catch {
      options.regions.center.style.visibility = '';
      return;
    }
    // WebKit otherwise retains the center surface's old backing-store pages
    // when Gridview changes its width. Separate the visibility invalidation
    // from the grid mutation by one frame boundary, then reveal only the newest
    // requested arrangement.
    options.regions.center.style.visibility = 'hidden';
    cancelAnimationFrame(toolsFrame);
    toolsFrame = requestAnimationFrame(() => {
      if (disposed || transitionVersion !== toolsTransitionVersion) return;
      try {
        const panel = api.getPanel('tools');
        if (present === Boolean(panel)) {
          options.regions.center.style.visibility = '';
          return;
        }
        if (!present) applyToolsExpanded(false);
        const sessions = regionWidth('sessions');
        if (panel) rememberedToolsWidth = panel.api.width;
        runSynchronized(() => {
          if (present) {
            addTools();
            setRegionWidth('tools', rememberedToolsWidth);
          } else if (panel) {
            api.removePanel(panel);
          }
          if (sessions !== null) setRegionWidth('sessions', sessions);
        });
        toolsFrame = requestAnimationFrame(() => {
          if (!disposed && transitionVersion === toolsTransitionVersion) {
            options.regions.center.style.visibility = '';
          }
        });
      } catch {
        if (transitionVersion === toolsTransitionVersion) {
          options.regions.center.style.visibility = '';
        }
      }
    });
  };

  /** Lock the center at no width and give the right column the rest, or put
   * both back. The sessions column keeps its width either way. */
  const applyToolsExpanded = (expanded: boolean): void => {
    const tools = regionWidth('tools');
    const center = regionWidth('center');
    if (expanded === toolsExpanded || tools === null || center === null) return;
    const sessions = regionWidth('sessions');
    runSynchronized(() => {
      if (expanded) {
        collapsedToolsWidth = tools;
        setRegionLimits('center', { minimumWidth: 0, maximumWidth: 0 });
        setRegionWidth('tools', tools + center, { maximumWidth: Number.MAX_SAFE_INTEGER });
      } else {
        setRegionLimits('center', { minimumWidth: CENTER_MIN_WIDTH, maximumWidth: Number.MAX_SAFE_INTEGER });
        setRegionWidth('tools', collapsedToolsWidth, { maximumWidth: TOOLS_MAX_WIDTH });
      }
      if (sessions !== null) setRegionWidth('sessions', sessions);
    });
    toolsExpanded = expanded;
  };

  /** Same one-frame WebKit invalidation as `setToolsPresent`: hide the center,
   * change the grid a frame later, reveal it a frame after that. */
  const setToolsExpanded = (expanded: boolean): void => {
    const transitionVersion = ++toolsTransitionVersion;
    options.regions.center.style.visibility = 'hidden';
    cancelAnimationFrame(toolsFrame);
    toolsFrame = requestAnimationFrame(() => {
      if (disposed || transitionVersion !== toolsTransitionVersion) return;
      applyToolsExpanded(expanded);
      toolsFrame = requestAnimationFrame(() => {
        if (!disposed && transitionVersion === toolsTransitionVersion) {
          options.regions.center.style.visibility = '';
        }
      });
    });
  };

  return {
    api,
    ready,
    setRegionWidth,
    setRegionHeight,
    setDockPresent,
    setToolsPresent,
    setToolsExpanded,
    setRegionLimits,
    regionWidth,
    resetLayout(): void {
      toolsExpanded = false;
      void persistLayout(null);
      runSynchronized(() => {
        api.clear();
        buildDefault();
      });
      persistSoon();
    },
    layout(width: number, height: number): void {
      // The grid lays out proportionally, so a narrower frame (the drawer
      // opening beside it, a smaller window) would take a share from the
      // sessions rail too. Asking for the rail's width again afterwards puts
      // the whole change on the center and the pane.
      const sessions = regionWidth('sessions');
      api.layout(width, height);
      if (sessions !== null && regionWidth('sessions') !== sessions) setRegionWidth('sessions', sessions);
    },
    dispose(): void {
      disposed = true;
      cancelAnimationFrame(toolsFrame);
      changeListener.dispose();
      api.dispose();
    }
  };
}
