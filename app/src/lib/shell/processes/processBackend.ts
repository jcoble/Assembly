/**
 * processBackend.ts — the one desktop command that stops a running process, and
 * the question that says whether this build of the app has it.
 *
 * Written the same way `worktreesBackend.ts` and `playwrightBackend.ts` are:
 * `@tauri-apps/api/core` is imported only inside the call, and the answer is
 * `null` when we are not running inside the desktop app. `null` means "nothing
 * was invoked" — the card then says the plain truth instead of inventing an
 * outcome.
 *
 * WHY THE APP IS ASKED WHAT IT CAN DO, RATHER THAN JUST TRYING.
 * Stopping a process is not undoable, so the button must never be offered by a
 * screen that cannot actually deliver it. A desktop build made before
 * `kill_process` existed answers the call with an error, and an error arriving
 * AFTER the user confirmed "yes, stop it" is a confirmation they should never
 * have been asked for. So the app is asked outright, once:
 * `read_backend_capabilities` lists what this build supports, and the stop
 * button only becomes live when `processKill` is in that list. A build too old
 * to even have that question answers with an error, which is caught here and
 * read as "it cannot".
 */
import { isNativeTauriRuntime } from '../../tauriSource.ts';

/** The name the desktop app uses for "this build can stop a process by id". */
export const PROCESS_KILL_CAPABILITY = 'processKill';

/** What the desktop app says happened, in a sentence meant to be shown as-is. */
export interface ProcessKillResult {
  /** Did the process actually get the signal? */
  ok: boolean;
  /** One plain sentence describing what happened. */
  message: string;
}

/**
 * Ask the desktop app to stop the process with this id.
 *
 * The desktop side refuses ids that would be dangerous (anything at or below 1,
 * and the app's own process), so the honest answer for those comes back as
 * `ok: false` with a sentence saying why, not as a thrown error.
 *
 * `expectedCommand` is the command the list showed for this id. Ids get handed
 * out again after a process exits, so the app re-checks the id still belongs
 * to that command before sending anything, and refuses if it moved on.
 */
export async function killProcess(
  pid: number,
  expectedCommand?: string
): Promise<ProcessKillResult | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<ProcessKillResult>('kill_process', { pid, expectedCommand: expectedCommand ?? null });
}

/**
 * What this build of the desktop app can do, as a list of names.
 *
 * `null` means we are not in the desktop app. An empty list means the app is
 * running but is too old to answer the question — which is itself the answer,
 * so the error is swallowed here rather than shown to anyone.
 */
export async function readBackendCapabilities(): Promise<string[] | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  try {
    const capabilities = await invoke<string[]>('read_backend_capabilities');
    return Array.isArray(capabilities)
      ? capabilities.filter((name) => typeof name === 'string')
      : [];
  } catch {
    // A build that has never heard of the question cannot do the new thing.
    return [];
  }
}
