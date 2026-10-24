import {
  INITIAL_ACTION_SURFACE_STATE,
  reduceActionSurface,
  type ActionSurfaceEvent,
  type ActionSurfaceState
} from './actionSurfaceModel.ts';

/** One shell-wide state holder; the FAB is the only mounted consumer today. */
export const actionSurface = $state<ActionSurfaceState>({ ...INITIAL_ACTION_SURFACE_STATE });

export function dispatchActionSurface(event: ActionSurfaceEvent): void {
  Object.assign(actionSurface, reduceActionSurface(actionSurface, event));
}

export function resetActionSurface(): void {
  Object.assign(actionSurface, INITIAL_ACTION_SURFACE_STATE);
}
