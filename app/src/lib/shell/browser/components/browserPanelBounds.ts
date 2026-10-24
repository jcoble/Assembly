/**
 * browserPanelBounds.ts — turning the panel's own rectangle into the bounds the
 * native browser view is given.
 *
 * The page in the Browser tab is not a DOM element the panel can lay out. It is
 * a native child view the shell positions by window-space bounds, so the panel
 * has to measure the box it wants filled and hand those numbers over. There is
 * one rectangle and one rule: the view fills the panel. Widening the page means
 * widening the right column — the panel then measures wider, and this turns
 * that into the bounds the view is given.
 *
 * PURE: no DOM, no backend call. The measuring happens in the component; the
 * arithmetic happens here so it can be tested without a window.
 */
import {
  browserBoundsEqual,
  clampBrowserFloatingBounds,
  type BrowserWindowSize
} from '../browserBounds.ts';
import type { BrowserFloatingBounds } from '../browserTypes.ts';

export type PanelRect = BrowserFloatingBounds;

/**
 * A view filling a panel has no minimum size. The floating browser has one — a
 * window too small to read is not worth showing — but a panel view held to that
 * floor is drawn wider than the panel and covers the pane beside it, which is
 * worse than being small. The panel is the authority on its own size.
 */
export const HOST_MIN_SIZE = { minWidth: 1, minHeight: 1 } as const;

/**
 * The bounds for a view that fills `rect`, kept inside the window.
 */
export function boundsForHost(rect: PanelRect, window: BrowserWindowSize): BrowserFloatingBounds {
  return clampBrowserFloatingBounds(rect, window, HOST_MIN_SIZE);
}

/** What the view is asked to do: fill a rectangle, or be off screen. */
export type HostPlacement =
  | { kind: 'hidden' }
  | { kind: 'bounds'; bounds: BrowserFloatingBounds };

export const HIDDEN_PLACEMENT: HostPlacement = { kind: 'hidden' };

/**
 * Whether two placements ask for the same thing. The panel re-measures on every
 * layout change; only a real difference is worth a call into the shell.
 */
export function samePlacement(a: HostPlacement, b: HostPlacement): boolean {
  if (a.kind !== b.kind) return false;
  if (a.kind !== 'bounds' || b.kind !== 'bounds') return true;
  return browserBoundsEqual(a.bounds, b.bounds);
}

/**
 * A rectangle worth handing over: on screen, and big enough to be a rectangle
 * at all. A panel that is hidden or mid-layout measures as an empty box, and a
 * view left where it was is a view drawn over something else.
 */
export function usableHostRect(rect: PanelRect | null): rect is PanelRect {
  return Boolean(
    rect &&
      Number.isFinite(rect.x) &&
      Number.isFinite(rect.y) &&
      rect.width >= 1 &&
      rect.height >= 1
  );
}

/**
 * Whether an open popup (menu, list, card or dialog) is drawn over the page.
 * The page is a native view above every DOM layer, so only a popup that
 * overlaps it needs it out of the way; one beside it leaves it showing. A
 * popup not laid out yet covers nothing, and a page that can't be measured
 * steps aside for any popup.
 */
export function popupCoversPage(popups: readonly PanelRect[], page: PanelRect | null): boolean {
  if (!page) return popups.length > 0;
  return popups.some(
    (p) =>
      p.width > 0 &&
      p.height > 0 &&
      p.x < page.x + page.width &&
      p.x + p.width > page.x &&
      p.y < page.y + page.height &&
      p.y + p.height > page.y
  );
}

/**
 * A docked page may fill its page host, but it may not escape the right tools
 * region or overlap the center. Gridview briefly moves adopted DOM while a
 * removed region is being restored; rejecting that intermediate rectangle
 * keeps the native child view from being placed over the whole shell.
 */
export function hostRectFitsPanel(
  rect: PanelRect,
  tools: PanelRect,
  center: PanelRect
): boolean {
  const slop = 1;
  return (
    rect.x >= tools.x - slop &&
    rect.y >= tools.y - slop &&
    rect.x + rect.width <= tools.x + tools.width + slop &&
    rect.y + rect.height <= tools.y + tools.height + slop &&
    rect.x >= center.x + center.width - slop
  );
}
