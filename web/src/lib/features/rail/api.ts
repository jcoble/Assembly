/**
 * Feature-owned .NET WebSocket RPCs & topic subscription for the Left Sessions Rail.
 * Uses the shared generic `backend` socket client; never touches `rows/` Chat traffic.
 */

import { backend, type TopicEvent } from '$lib/infrastructure/backend';
import type { RailArgs, RailChange, RailPage, SessionMarkState } from './types';

export const RAIL_PREF_FILTERS_KEY = 'rail.filters';
export const RAIL_PREF_VIEW_KEY = 'rail.view';

/**
 * Reads the SQL-ordered rail page from `.NET` `sessions/rail`.
 * Keyed on `sessions/rail` so rapid filter/view changes cancel stale reads on the backend too.
 */
export function fetchRailPage(args: RailArgs): Promise<RailPage> {
  return backend.call<RailPage>('sessions', 'rail', { ...args }, { key: 'sessions/rail' });
}

/**
 * Moves a session between lifecycle shelves (`working`, `done`, `settled`).
 * Intentionally unkeyed so consecutive marks each run to completion.
 */
export function markSessionState(sessionId: string, state: SessionMarkState): Promise<{ ok?: boolean }> {
  return backend.call<{ ok?: boolean }>('sessions', 'mark', {
    session: sessionId,
    state
  });
}

/**
 * Subscribes to `.NET`'s `sessionRail` invalidation topic (fires on subscribe, reconnect, and any session change).
 */
export function subscribeRailInvalidations(
  listener: (event: TopicEvent<RailChange>) => void
): () => void {
  return backend.subscribe<RailChange>('sessionRail', listener);
}
