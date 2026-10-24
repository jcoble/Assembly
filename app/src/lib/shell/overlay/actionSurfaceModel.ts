import type { Component } from 'svelte';

/** The action surface is shared by the shell and deliberately does not know
 * about any one panel's business logic. */
export type WorkbenchActionContextKind =
  | 'session'
  | 'editor'
  | 'browser'
  | 'resources'
  | 'history';

export type ActionSurfaceMode = 'collapsed' | 'fan-open' | 'action-pending';

/** Kept as a string so this model does not pull layout or Dockview code into
 * the Node-testable action surface contract. */
export type CenterPanelId = string;

export interface WorkbenchActionContext {
  kind: WorkbenchActionContextKind;
  centerPanelId: CenterPanelId;
  ownedId: string | null;
  workspaceId: string | null;
  targetId: string | null;
  generation: number | null;
}

export interface WorkbenchAction {
  id: string;
  label: string;
  icon: Component;
  contexts: WorkbenchActionContextKind[];
  shortcut: string | null;
  confirmation: 'none' | 'preview' | 'destructive';
  enabled(context: WorkbenchActionContext): { enabled: boolean; reason: string | null };
  run(context: WorkbenchActionContext): Promise<void>;
}

export type ActionFanLayout =
  | { kind: 'fan'; items: Array<{ id: string; x: number; y: number }> }
  | { kind: 'horizontal'; items: Array<{ id: string; x: number; y: number }> }
  | { kind: 'bottom-sheet'; items: string[] };

export interface ActionSurfaceState {
  mode: ActionSurfaceMode;
  pendingActionId: string | null;
  error: string | null;
}

export type ActionSurfaceEvent =
  | { type: 'toggle' }
  | { type: 'open' }
  | { type: 'close' }
  | { type: 'start-action'; actionId: string }
  | { type: 'finish-action' }
  | { type: 'fail-action'; message: string };

export const INITIAL_ACTION_SURFACE_STATE: ActionSurfaceState = {
  mode: 'collapsed',
  pendingActionId: null,
  error: null
};

/**
 * Pure state transitions for the global action island. The action itself is
 * always supplied by the caller; this reducer only controls visibility and
 * pending/error feedback.
 */
export function reduceActionSurface(
  state: ActionSurfaceState,
  event: ActionSurfaceEvent
): ActionSurfaceState {
  switch (event.type) {
    case 'toggle':
      return state.mode === 'collapsed'
        ? { ...state, mode: 'fan-open', error: null }
        : { ...INITIAL_ACTION_SURFACE_STATE };
    case 'open':
      return { ...state, mode: 'fan-open', pendingActionId: null, error: null };
    case 'close':
      return { ...INITIAL_ACTION_SURFACE_STATE };
    case 'start-action':
      return { ...state, mode: 'action-pending', pendingActionId: event.actionId, error: null };
    case 'finish-action':
      return { ...INITIAL_ACTION_SURFACE_STATE };
    case 'fail-action':
      return { ...state, mode: 'fan-open', pendingActionId: null, error: event.message };
  }
}

/** Keep disabled actions visible so the user can read why they are unavailable. */
export function actionsForContext(
  actions: readonly WorkbenchAction[],
  context: WorkbenchActionContext
): WorkbenchAction[] {
  return actions.filter((action) => action.contexts.includes(context.kind));
}

export interface ActionSurfaceViewport {
  width: number;
  height: number;
  safeArea?: { top?: number; right?: number; bottom?: number; left?: number };
  /** Accept the alternate spelling used by viewport observers. */
  safeAreaInsets?: { top?: number; right?: number; bottom?: number; left?: number };
}

export interface ActionSurfacePoint {
  x: number;
  y: number;
}

export interface ActionSurfaceItemSize {
  id: string;
  width: number;
  height: number;
}

export interface ActionSurfaceRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

type NormalizedRect = ActionSurfaceRect;

function finite(value: number | undefined, fallback: number): number {
  return Number.isFinite(value) ? Math.max(0, value as number) : fallback;
}

function normalizeSize(value: ActionSurfaceItemSize): ActionSurfaceItemSize {
  return {
    id: value.id,
    width: Math.max(1, finite(value.width, 48)),
    height: Math.max(1, finite(value.height, 48))
  };
}

function overlaps(a: NormalizedRect, b: NormalizedRect): boolean {
  return !(
    a.x + a.width <= b.x ||
    b.x + b.width <= a.x ||
    a.y + a.height <= b.y ||
    b.y + b.height <= a.y
  );
}

function reachable(
  items: Array<{ id: string; x: number; y: number }>,
  sizes: readonly ActionSurfaceItemSize[],
  safe: NormalizedRect,
  occlusionRects: readonly NormalizedRect[]
): boolean {
  const sizeById = new Map(sizes.map((item) => [item.id, item]));
  const placed: NormalizedRect[] = [];
  for (const item of items) {
    const size = sizeById.get(item.id);
    if (!size) return false;
    const rect = {
      x: item.x - size.width / 2,
      y: item.y - size.height / 2,
      width: size.width,
      height: size.height
    };
    if (
      rect.x < safe.x ||
      rect.y < safe.y ||
      rect.x + rect.width > safe.x + safe.width ||
      rect.y + rect.height > safe.y + safe.height ||
      occlusionRects.some((occlusion) => overlaps(rect, occlusion)) ||
      placed.some((other) => overlaps(rect, other))
    ) {
      return false;
    }
    placed.push(rect);
  }
  return true;
}

function fanCandidates(
  anchor: ActionSurfacePoint,
  sizes: readonly ActionSurfaceItemSize[]
): Array<{ id: string; x: number; y: number }> {
  const count = sizes.length;
  if (count === 0) return [];
  const radius = 76;
  const start = count === 1 ? -Math.PI / 2 : -Math.PI;
  const end = count === 1 ? -Math.PI / 2 : -Math.PI / 2;
  return sizes.map((size, index) => {
    const angle = count === 1 ? start : start + ((end - start) * index) / (count - 1);
    return {
      id: size.id,
      x: anchor.x + Math.cos(angle) * radius,
      y: anchor.y + Math.sin(angle) * radius
    };
  });
}

function horizontalCandidates(
  anchor: ActionSurfacePoint,
  sizes: readonly ActionSurfaceItemSize[],
  gap = 8
): Array<{ id: string; x: number; y: number }> {
  const totalWidth = sizes.reduce((total, size) => total + size.width, 0) + Math.max(0, sizes.length - 1) * gap;
  let cursor = anchor.x - totalWidth / 2;
  return sizes.map((size) => {
    const item = { id: size.id, x: cursor + size.width / 2, y: anchor.y - 72 - size.height / 2 };
    cursor += size.width + gap;
    return item;
  });
}

/**
 * Place action targets without clipping them. The fan is attempted first,
 * then a single horizontal row, and finally a labelled bottom sheet when an
 * open dock/overlay or a small viewport leaves no safe geometry.
 */
export function layoutActionFan(
  viewport: ActionSurfaceViewport,
  anchor: ActionSurfacePoint,
  itemSizes: readonly ActionSurfaceItemSize[],
  occlusionRects: readonly ActionSurfaceRect[] = []
): ActionFanLayout {
  const sizes = itemSizes.map(normalizeSize);
  const safeArea = viewport.safeArea ?? viewport.safeAreaInsets ?? {};
  const margin = 16;
  const safe: NormalizedRect = {
    x: margin + finite(safeArea.left, 0),
    y: margin + finite(safeArea.top, 0),
    width: Math.max(0, finite(viewport.width, 0) - margin * 2 - finite(safeArea.left, 0) - finite(safeArea.right, 0)),
    height: Math.max(0, finite(viewport.height, 0) - margin * 2 - finite(safeArea.top, 0) - finite(safeArea.bottom, 0))
  };
  const occlusions = occlusionRects.map((rect) => ({ ...rect }));
  const fan = fanCandidates(anchor, sizes);
  if (reachable(fan, sizes, safe, occlusions)) return { kind: 'fan', items: fan };

  const horizontal = horizontalCandidates(anchor, sizes);
  if (reachable(horizontal, sizes, safe, occlusions)) return { kind: 'horizontal', items: horizontal };

  return { kind: 'bottom-sheet', items: sizes.map((size) => size.id) };
}
