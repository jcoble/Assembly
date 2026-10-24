/**
 * problemsBackend.ts — the Problems panel's own way of reaching the desktop.
 *
 * The panel needs one command the app has only just grown,
 * `list_source_lsp_diagnostics_for_root`, which returns everything the language
 * servers under a project folder have reported. A desktop app built before that
 * command existed rejects the call, and the panel has to keep working there —
 * so every reach for it goes through this file, which answers three different
 * ways and never lies about which one happened:
 *
 *  - `null` — not running in the desktop app at all (a browser tab). Nothing was
 *    invoked and there is nothing to report.
 *  - `{ unavailable: true }` — the desktop app is running but does not have the
 *    whole-project command. The panel falls back to asking about the files you
 *    have open, and says so on screen.
 *  - `{ diagnostics }` — the real answer, which may be an empty list.
 *
 * This is a lane-local wrapper on purpose (the wave rule: a lane adds its own
 * `<lane>Backend.ts` rather than editing the shared `tauriSource.ts`). The
 * integrator may fold it into `tauriSource.ts` later; the shapes are the same.
 */
import { isNativeTauriRuntime, readSourceLspDiagnosticsFromTauri } from '../../tauriSource.ts';
import type { SourcePreview } from '../../sourceData.ts';
import type { RawProblemDiagnostic } from './problemsStore.svelte.ts';

/** The name is repeated in the error check below, so it lives here once. */
const WHOLE_PROJECT_COMMAND = 'list_source_lsp_diagnostics_for_root';

/** What a whole-project read came back with. */
export type WholeProjectProblems =
  | { unavailable: true; diagnostics?: undefined }
  | { unavailable: false; diagnostics: RawProblemDiagnostic[] };

/**
 * Is this error the desktop app saying "I have never heard of that command"?
 *
 * Tauri answers an unregistered command with a message naming it, and the exact
 * wording has changed between versions, so the check looks for the command name
 * together with any of the ways a version has said "no such thing". Anything
 * else is a real failure and is reported as one — a genuine language-server
 * crash must not be quietly downgraded to "your desktop app is old".
 */
export function isUnknownCommandError(error: unknown): boolean {
  const message = (error instanceof Error ? error.message : String(error ?? '')).toLowerCase();
  if (!message.includes(WHOLE_PROJECT_COMMAND)) return false;
  return (
    message.includes('not found') ||
    message.includes('not allowed') ||
    message.includes('unknown') ||
    message.includes('does not exist') ||
    message.includes('not registered')
  );
}

/**
 * Everything the language servers under `root` have reported.
 *
 * `null` off the desktop. `{ unavailable: true }` when the desktop app predates
 * the command. Any other failure is thrown for the caller to word.
 */
export async function listProblemsForRoot(root: string): Promise<WholeProjectProblems | null> {
  if (!isNativeTauriRuntime()) return null;

  const { invoke } = await import('@tauri-apps/api/core');
  try {
    const diagnostics = await invoke<RawProblemDiagnostic[]>(WHOLE_PROJECT_COMMAND, { root });
    return { unavailable: false, diagnostics: diagnostics ?? [] };
  } catch (error) {
    if (isUnknownCommandError(error)) return { unavailable: true };
    throw error;
  }
}

/**
 * The fallback: what the language server has to say about ONE file.
 *
 * This is the command the old shell has always used (and the only one an older
 * desktop app has), so the panel can still show something real there. `null`
 * off the desktop.
 */
export async function readProblemsForFile(
  preview: SourcePreview,
  root: string
): Promise<RawProblemDiagnostic[] | null> {
  const diagnostics = await readSourceLspDiagnosticsFromTauri(preview, {
    root,
    line: 1,
    column: 1
  });
  return diagnostics as RawProblemDiagnostic[] | null;
}
