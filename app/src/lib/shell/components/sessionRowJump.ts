/**
 * sessionRowJump.ts — one click from a left-rail row to that session on the
 * exact surface you wanted.
 *
 * Two halves, on purpose. `planSessionRowJump` is a pure mapping from a row and
 * a surface name to the ids the shell has to open, so it can be read and tested
 * without a browser. `sessionRowJump` is the thin dispatch that runs a plan
 * against whichever host is currently registered.
 *
 * There is a host at all because the rail and the surfaces it points at are not
 * siblings: the row lives in the left column, the center tabs are owned by the
 * frame, and the tool column owns its own views. The page is the one place that
 * can reach all three, so it registers itself here — the same shape as
 * `conversation/sessionRestart.ts` and `history/library/sessionLibraryNavigation.ts`.
 */

/** The surfaces a row can send you to. */
export const SESSION_ROW_SURFACES = ['session', 'editor', 'source-control'] as const;

export type SessionRowSurface = (typeof SESSION_ROW_SURFACES)[number];

export interface SessionRowJumpPlan {
  ownedId: string;
  /** Center dock tab to activate, or null when the surface does not live there. */
  centerPanelId: 'session' | 'editor' | null;
  /** Tool-column view to open, or null. */
  sidebarViewId: 'source-control' | null;
}

export interface SessionRowJumpTarget {
  selectSession(ownedId: string): Promise<void>;
  showCenterPanel(ownedId: string, id: 'session' | 'editor'): void;
  showSidebarView(ownedId: string, id: 'source-control'): void;
}

export function isSessionRowSurface(value: unknown): value is SessionRowSurface {
  return SESSION_ROW_SURFACES.some((surface) => surface === value);
}

/**
 * What a jump means, in ids. Session and Editor are center tabs; source control
 * is a tool-column view — the Diff tab beside them shows one file's changes and
 * opens empty until a file is picked, so "Git" from a row opens the changed-file
 * list rather than a blank diff.
 *
 * Returns null for anything unopenable, which the dispatch below treats as "do
 * nothing" rather than half a jump.
 */
export function planSessionRowJump(
  ownedId: unknown,
  surface: unknown
): SessionRowJumpPlan | null {
  if (typeof ownedId !== 'string') return null;
  const id = ownedId.trim();
  if (!id) return null;
  if (!isSessionRowSurface(surface)) return null;

  if (surface === 'source-control') {
    return { ownedId: id, centerPanelId: null, sidebarViewId: 'source-control' };
  }
  return { ownedId: id, centerPanelId: surface, sidebarViewId: null };
}

let activeTarget: SessionRowJumpTarget | null = null;

/**
 * Share the page-owned session activation and surface selection with the rail.
 * The returned release only clears the binding it made, so a host that has
 * already been replaced cannot unregister its successor on teardown.
 */
export function registerSessionRowJumpTarget(target: SessionRowJumpTarget): () => void {
  activeTarget = target;
  return () => {
    if (activeTarget === target) activeTarget = null;
  };
}

/**
 * Activate the session, then select the surface — in that order, so the surface
 * opens already showing the session the row named. False means nothing ran:
 * either the jump could not be planned or no host is registered.
 */
export async function sessionRowJump(ownedId: unknown, surface: unknown): Promise<boolean> {
  const plan = planSessionRowJump(ownedId, surface);
  if (!plan || !activeTarget) return false;
  await activeTarget.selectSession(plan.ownedId);
  if (plan.centerPanelId) activeTarget.showCenterPanel(plan.ownedId, plan.centerPanelId);
  if (plan.sidebarViewId) activeTarget.showSidebarView(plan.ownedId, plan.sidebarViewId);
  return true;
}
