/**
 * playwrightBackend.ts — the three desktop commands the Playwright card needs.
 *
 * WHY THIS FILE EXISTS AND NOT `tauriSource.ts`
 * This lane may only add files, so its backend wrappers live here. Same
 * convention as `tauriSource.ts`: each wrapper returns `null` when the app is
 * running in a plain browser — that means "nothing was invoked", NOT "there is
 * nothing running". The card says so in words instead of showing an empty list.
 *
 * THE THREE COMMANDS
 *  - `list_playwright_sessions` — the read. Runs `ps` in the desktop app and
 *    keeps only processes it can positively tie to Playwright.
 *  - `kill_playwright_sessions` — stop everything it just listed.
 *  - `kill_playwright_session`  — stop ONE listed process group. Newer than the
 *    other two, so an older desktop build will not have it; see
 *    `isMissingCommandError`.
 *
 * Neither stop command can be pointed at an arbitrary process: the desktop side
 * only ever signals process ids it listed as Playwright's in the same call.
 */
import type { PlaywrightCleanupResult, PlaywrightSessionInfo } from '../../tauriSource.ts';

export type {
  PlaywrightCleanupFailure,
  PlaywrightCleanupResult,
  PlaywrightProcessInfo,
  PlaywrightSessionInfo
} from '../../tauriSource.ts';

/** `true` inside the desktop app, `false` in a browser tab. */
export function isDesktopRuntime(): boolean {
  return typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window;
}

/** Every Playwright process group the desktop app can see. `null` = browser. */
export async function listPlaywrightSessions(): Promise<PlaywrightSessionInfo[] | null> {
  if (!isDesktopRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<PlaywrightSessionInfo[]>('list_playwright_sessions');
}

/** Stop every listed Playwright process group. `null` = browser. */
export async function killAllPlaywrightSessions(): Promise<PlaywrightCleanupResult | null> {
  if (!isDesktopRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<PlaywrightCleanupResult>('kill_playwright_sessions');
}

/**
 * Stop ONE listed process group. `null` = browser.
 *
 * Throws when the desktop build does not have this command yet — the caller
 * checks with `isMissingCommandError` and falls back to hiding the per-session
 * button, because stopping everything still works.
 */
export async function killPlaywrightSession(pgid: number): Promise<PlaywrightCleanupResult | null> {
  if (!isDesktopRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<PlaywrightCleanupResult>('kill_playwright_session', { pgid });
}

/**
 * A process group id that can never match a real session, used to ask the
 * desktop app whether it has `kill_playwright_session` WITHOUT stopping
 * anything. Process group ids are never negative, and the command refuses any
 * group it did not just list, so this probe is inert by construction.
 */
export const PROBE_PGID = -1;

/**
 * Does this error mean "this desktop build does not have that command", rather
 * than "the command ran and said no"?
 *
 * Tauri answers an unregistered command with a message about the command not
 * being found, and a blocked one with a message about it not being allowed.
 * Everything the command itself returns is an ordinary sentence about
 * Playwright, so those two families are safe to tell apart by wording.
 */
export function isMissingCommandError(error: unknown): boolean {
  const message = (error instanceof Error ? error.message : String(error ?? '')).toLowerCase();
  if (!message) return false;
  return (
    message.includes('not found') ||
    message.includes('not allowed') ||
    message.includes('unknown command') ||
    message.includes('command kill_playwright_session') ||
    message.includes('not registered')
  );
}
