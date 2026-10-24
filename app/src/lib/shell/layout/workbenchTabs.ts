/**
 * workbenchTabs.ts — which drawer tab each session was left on.
 *
 * The right drawer remembers its selected tab in the active session's SQLite
 * workspace snapshot (the top tab row has its own record, `topTabsOps.ts`).
 * This pure module owns only the valid values and default; it has no
 * persistence path of its own.
 */
import { RIGHT_TAB_IDS, type RightTabId } from '../workbenchNavigation.ts';

export const DEFAULT_RIGHT_TAB: RightTabId = 'files';

export function isRightTabId(value: unknown): value is RightTabId {
  return typeof value === 'string' && RIGHT_TAB_IDS.some((id) => id === value);
}
