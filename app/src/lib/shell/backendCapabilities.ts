/**
 * backendCapabilities.ts — what this build of the desktop app can do.
 *
 * The desktop app answers `read_backend_capabilities` with a list of names. A
 * build too old to have heard of the question, or a browser tab with no desktop
 * app behind it at all, can do none of the new things — so both come back as an
 * empty list rather than an error.
 *
 * WHY A LIST AND NOT A TRY-IT-AND-SEE. Tauri drops fields a command does not
 * declare, so sending a new field to an old build does not fail: it quietly does
 * the old thing. Asking outright is the only way to tell the two apart.
 *
 * The answer is read once and remembered for the life of the page. The desktop
 * app cannot change underneath a running window — a new build means a restart,
 * which means a new page.
 */
import { isNativeTauriRuntime } from '../tauriSource.ts';

/** The one in-flight or finished read, or `null` before anything has asked. */
let pending: Promise<readonly string[]> | null = null;

/** Ask the desktop app what it can do. Safe to call as often as you like. */
export async function readBackendCapabilities(): Promise<readonly string[]> {
  pending ??= readBackendCapabilitiesOnce();
  return pending;
}

async function readBackendCapabilitiesOnce(): Promise<readonly string[]> {
  if (!isNativeTauriRuntime()) return [];
  try {
    const { invoke } = await import('@tauri-apps/api/core');
    const answer = await invoke<string[]>('read_backend_capabilities');
    return Array.isArray(answer) ? answer.filter((name) => typeof name === 'string') : [];
  } catch {
    // A build that has never heard of the question cannot do the new thing.
    return [];
  }
}

/** Can this desktop build do the named thing? */
export async function hasBackendCapability(name: string): Promise<boolean> {
  return (await readBackendCapabilities()).includes(name);
}

/** Forget the answer. For tests only; nothing in the shell needs it. */
export function forgetBackendCapabilities(): void {
  pending = null;
}
