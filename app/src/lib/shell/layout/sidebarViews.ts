/**
 * sidebarViews.ts — which views the tool column offers, and which one it opens.
 *
 * PURE: no DOM, no dockview, no backend call. The activity bar and the column
 * itself both read the roster from here so an id can never mean two different
 * things in the two places.
 *
 * Each view holds its own stack of collapsible panes and remembers it under its
 * own key, so opening Explorer cannot disturb how Worktrees was arranged.
 *
 * Two keys here are deliberately never read again, both left where they are,
 * harmless and unread — the same treatment the center dock's pre-v2 layout key
 * got:
 *
 *  - `mac-command-bar.next.left-panes`, from the single shared stack these
 *    replaced. It describes one stack of four sections, a shape the column no
 *    longer builds.
 *  - `mac-command-bar.next.view-sessions-panes`, from back when the sessions
 *    list was one of this column's views. Sessions now has a whole column of
 *    its own down the left of the shell, so there is no view here to restore it
 *    into. A stored "open on the sessions view" answer is handled the same way
 *    by `readActiveView`: it names a view that no longer exists, so the column
 *    opens on the default instead.
 */
import { clearLayout, loadLayout, saveLayout, type LayoutStorage } from './layoutStorage.ts';

export type SidebarViewId =
  | 'explorer'
  | 'source-control'
  | 'worktrees'
  | 'stacks'
  | 'context'
  | 'agents'
  | 'problems';

export interface SidebarView {
  id: SidebarViewId;
  /** What the icon's tooltip says. */
  title: string;
}

/**
 * The Problems list, which the user can ask for in this column rather than in
 * the strip along the bottom (`settings.panels.problemsLocation === 'right'`).
 */
export const PROBLEMS_VIEW_ID: SidebarViewId = 'problems';

/** Its row in the roster below. */
export const PROBLEMS_SIDEBAR_VIEW: SidebarView = {
  id: PROBLEMS_VIEW_ID,
  title: 'Problems'
};

/** The collapsible pane the tool column builds for it, matching `PANES` in
 * `ShellSidebar.svelte` (`{ id, title }`, one per view). */
export const PROBLEMS_SIDEBAR_PANE: { id: string; title: string } = {
  id: 'problems',
  title: 'Problems'
};

/**
 * Top to bottom, in the order the activity bar draws them.
 *
 * Problems is always in this list even though its icon usually is not. The tool
 * column builds one container per row here, once, when it mounts; a roster that
 * grew and shrank with a setting would tear those containers down and put them
 * back, losing whatever was in them. So the container is always there and the
 * ACTIVITY BAR decides whether to offer the icon — see `ActivityBar.svelte`,
 * which draws Problems only while the setting asks for it.
 */
export const SIDEBAR_VIEWS: readonly SidebarView[] = [
  { id: 'explorer', title: 'Explorer' },
  { id: 'source-control', title: 'Source control' },
  { id: 'worktrees', title: 'Worktrees' },
  { id: 'stacks', title: 'Run' },
  { id: 'context', title: 'Context' },
  { id: 'agents', title: 'Agents' },
  PROBLEMS_SIDEBAR_VIEW
];

/** The view a first launch opens on, and the one a reset goes back to. */
export const DEFAULT_SIDEBAR_VIEW: SidebarViewId = 'explorer';

export const ACTIVE_VIEW_KEY = 'mac-command-bar.next.active-view';

export function isSidebarViewId(value: unknown): value is SidebarViewId {
  return typeof value === 'string' && SIDEBAR_VIEWS.some((view) => view.id === value);
}

/** Where one view's pane sizes and folded/open states are remembered. */
export function viewPanesKey(id: SidebarViewId): string {
  return `mac-command-bar.next.view-${id}-panes`;
}

/**
 * The view to open. Anything unreadable, corrupt, or naming a view that no
 * longer exists comes back as the default — a stored id must never be able to
 * open the column on nothing.
 */
export function readActiveView(storage: LayoutStorage): SidebarViewId {
  const stored = loadLayout<unknown>(storage, ACTIVE_VIEW_KEY);
  return isSidebarViewId(stored) ? stored : DEFAULT_SIDEBAR_VIEW;
}

/** Remember the open view. False means the write was refused (a full storage);
 * the column carries on regardless — the cost is only that the next launch
 * opens somewhere else. */
export function writeActiveView(storage: LayoutStorage, id: SidebarViewId): boolean {
  return saveLayout(storage, ACTIVE_VIEW_KEY, id);
}

export function clearActiveView(storage: LayoutStorage): void {
  clearLayout(storage, ACTIVE_VIEW_KEY);
}
