/**
 * playwrightService.ts — the ONLY place the Playwright card talks to the backend.
 *
 * Imperative: nothing here runs from an `$effect`, nothing runs at import, and
 * nothing polls. Reads happen exactly twice:
 *
 *  - the shell calls `activate()` when the Resources panel is opened;
 *  - the user presses refresh on the card, or stops something (a stop always
 *    re-reads, because the stop result lists what was ASKED to stop, not what
 *    survived).
 *
 * Every backend call is counted with `countInvoke('<command name>')` right
 * before it, so the dev invoke counter stays honest.
 *
 * A `null` from a wrapper means "not running in the desktop app" — nothing was
 * invoked. The card says so; it never shows made-up rows.
 */
import { countInvoke } from '../devInvokeCounter.svelte.ts';
import {
  isMissingCommandError,
  killAllPlaywrightSessions,
  killPlaywrightSession,
  listPlaywrightSessions,
  PROBE_PGID
} from './playwrightBackend.ts';
import {
  applyPlaywrightGroups,
  beginPlaywrightLoad,
  beginStopAll,
  beginStopSession,
  buildPlaywrightGroups,
  describeCleanupOutcome,
  failPlaywrightLoad,
  finishStop,
  markPerSessionStopSupported,
  markPerSessionStopUnsupported,
  markPlaywrightActivated,
  markPlaywrightUnavailable,
  playwrightState,
  resetPlaywright
} from './playwrightStore.svelte.ts';

/** Shown when the data only exists inside the desktop app. */
const DESKTOP_ONLY = 'This runs in the desktop app only.';

/** Said once when the desktop build is too old to stop one session at a time. */
const NO_PER_SESSION_STOP =
  'This desktop build can only stop every Playwright session at once. Use “Stop all Playwright”.';

/** Has a read already run since the shell last mounted the card? */
let loadedOnce = false;

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * Show the card and read the process list — once. Calling it again does no
 * work, so the shell may call it every time the Resources panel is opened.
 */
export function activate(): void {
  markPlaywrightActivated();
  if (loadedOnce) return;
  loadedOnce = true;
  void refresh();
}

/** Read the process list again. The card's refresh button. */
export async function refresh(): Promise<void> {
  const ticket = beginPlaywrightLoad();
  try {
    countInvoke('list_playwright_sessions');
    const sessions = await listPlaywrightSessions();
    if (sessions === null) {
      markPlaywrightUnavailable(ticket, DESKTOP_ONLY);
      return;
    }
    applyPlaywrightGroups(ticket, buildPlaywrightGroups(sessions));
    await probePerSessionStop();
  } catch (error) {
    failPlaywrightLoad(ticket, `Could not read Playwright processes: ${describeError(error)}`);
  }
}

/**
 * Let go of the card's state — used when the shell tears the panel down.
 *
 * Forgetting the read is not enough on its own: the process groups from the
 * last read stay in the store, holding a row per process for a card that is no
 * longer on screen. Dropping the store back to launch state releases them, and
 * clearing `loadedOnce` means the next mount reads the processes again instead
 * of showing the list as it was when the panel closed.
 */
export function resetPlaywrightActivation(): void {
  loadedOnce = false;
  resetPlaywright();
}

/**
 * Stop one process group, then re-read.
 *
 * The result the desktop app returns lists the sessions it was ASKED to stop,
 * so it cannot be used as the new list — the card reads the processes again
 * instead and shows what is genuinely still running.
 */
export async function stopSession(pgid: number): Promise<void> {
  beginStopSession(pgid);
  try {
    countInvoke('kill_playwright_session');
    const result = await killPlaywrightSession(pgid);
    if (result === null) {
      finishStop(DESKTOP_ONLY);
      return;
    }
    markPerSessionStopSupported();
    finishStop(describeCleanupOutcome(result));
  } catch (error) {
    if (isMissingCommandError(error)) {
      markPerSessionStopUnsupported();
      finishStop(NO_PER_SESSION_STOP);
      return;
    }
    finishStop(`Could not stop that session: ${describeError(error)}`);
  }
  await refresh();
}

/** Stop every listed process group, then re-read. */
export async function stopAll(): Promise<void> {
  beginStopAll();
  try {
    countInvoke('kill_playwright_sessions');
    const result = await killAllPlaywrightSessions();
    if (result === null) {
      finishStop(DESKTOP_ONLY);
      return;
    }
    finishStop(describeCleanupOutcome(result));
  } catch (error) {
    finishStop(`Could not stop Playwright: ${describeError(error)}`);
  }
  await refresh();
}

/**
 * Ask, once, whether this desktop build can stop ONE session.
 *
 * `kill_playwright_session` is newer than the other two commands, so a desktop
 * app built before it answers "no such command". The question is asked with a
 * process group id that can never match anything (`PROBE_PGID`, negative), so
 * the probe never stops a thing: a build that HAS the command answers "no
 * Playwright session is running in that group", which is exactly the proof we
 * wanted.
 */
async function probePerSessionStop(): Promise<void> {
  if (playwrightState.perSessionStopSupported !== 'unknown') return;
  try {
    countInvoke('kill_playwright_session');
    const result = await killPlaywrightSession(PROBE_PGID);
    // Only reachable in a browser (`null`), where the question is moot.
    if (result !== null) markPerSessionStopSupported();
  } catch (error) {
    if (isMissingCommandError(error)) markPerSessionStopUnsupported();
    else markPerSessionStopSupported();
  }
}
