/**
 * problemsService.ts — the ONLY place the Problems panel talks to the backend.
 *
 * One loader, driven imperatively. Nothing here runs from an `$effect`, nothing
 * runs at import, and nothing polls. A load happens exactly twice:
 *
 *  - the shell calls `activate(root)` when the Problems panel is first opened
 *    (and again when the picked session changes while it is open);
 *  - the user presses Refresh in the panel, which calls `refresh()`.
 *
 * That is the constitution's "nothing loads at launch" rule: opening the app
 * asks the language server nothing at all.
 *
 * Two routes, in order:
 *
 *  1. **The whole project.** One call lists everything the language servers
 *     under this folder have reported.
 *  2. **The files you have open.** A desktop app built before the whole-project
 *     command exists rejects it, so the panel asks about each open file instead
 *     — the same per-file read the old shell has always used. The panel says
 *     out loud that it only looked at those files, because a short list from
 *     this route does NOT mean the project is nearly clean.
 *
 * Every backend call is counted with `countInvoke('<command name>')` immediately
 * before it, so the dev invoke counter stays honest. A `null` from a wrapper
 * means "not running in the desktop app" — nothing was invoked, and the panel
 * says so rather than showing made-up problems.
 */
import { sourceSupportsLanguageIntelligence } from '../../sourceData.ts';
import { countInvoke } from '../devInvokeCounter.svelte.ts';
import { editorState } from '../files/editor/editorStore.svelte.ts';
import { listProblemsForRoot, readProblemsForFile } from './problemsBackend.ts';
import { nativeCsharpDiagnosticsForRoot } from './csharpDiagnosticsAdapter.ts';
import {
  applyProblems,
  beginProblemsLoad,
  failProblemsLoad,
  markProblemsActivated,
  markProblemsUnavailable,
  problemRowFromDiagnostic,
  problemsState,
  setProblemsRoot,
  type ProblemRow
} from './problemsStore.svelte.ts';

/** Shown when the data only exists inside the desktop app. */
const DESKTOP_ONLY = 'This runs in the desktop app only.';

/** The folder the last load was for, so re-opening the panel costs nothing. */
let loadedRoot: string | null = null;

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * Show the Problems panel and load it.
 *
 * Idempotent: opening the panel again on the same project does no work at all.
 * A different project reloads. Pass a root of `null` (no session picked) and
 * the panel is pointed at nothing and stays quiet.
 */
export function activate(root: string | null): void {
  const next = (root ?? '').trim() || null;
  const firstTime = !problemsState.activated;
  setProblemsRoot(next);
  markProblemsActivated();
  if (!firstTime && next === loadedRoot) return;
  loadedRoot = next;
  void load();
}

/** Reload. The panel's Refresh button, and nothing else. */
export async function refresh(): Promise<void> {
  markProblemsActivated();
  loadedRoot = problemsState.root;
  await load();
}

/** Forget which project was last loaded — used when the shell tears down. */
export function resetProblemsActivation(): void {
  loadedRoot = null;
}

// ── The one loader ────────────────────────────────────────────────────────────

async function load(): Promise<void> {
  const ticket = beginProblemsLoad();
  const root = problemsState.root;
  if (!root) {
    markProblemsUnavailable(ticket, 'Pick a session to see its problems.');
    return;
  }

  try {
    countInvoke('list_source_lsp_diagnostics_for_root');
    const answer = await listProblemsForRoot(root);
    if (answer === null) {
      markProblemsUnavailable(ticket, DESKTOP_ONLY);
      return;
    }
    if (answer.unavailable) {
      await loadFromOpenFiles(ticket, root);
      return;
    }
    applyProblems(
      ticket,
      rowsFrom([...answer.diagnostics, ...nativeCsharpDiagnosticsForRoot(root)], root),
      'workspace',
      0
    );
  } catch (error) {
    failProblemsLoad(ticket, `Could not read problems: ${describeError(error)}`);
  }
}

/**
 * The fallback route: ask about each open file, one call per file.
 *
 * Only files with a language server behind them are asked about — asking about
 * a plain text file spawns nothing and returns nothing, but it does cost a
 * round trip per file, and the count on screen has to mean "files we actually
 * looked at" for the sentence under the header to be true.
 */
async function loadFromOpenFiles(ticket: number, root: string): Promise<void> {
  const files = editorState.openFiles.filter(
    (file) => file.preview !== null && sourceSupportsLanguageIntelligence(file.language)
  );

  if (files.length === 0) {
    applyProblems(ticket, [], 'open-files', 0);
    return;
  }

  const rows: ProblemRow[] = [];
  let filesRead = 0;
  let lastError: string | null = null;

  for (const file of files) {
    const preview = file.preview;
    if (!preview) continue;
    try {
      countInvoke('read_source_lsp_diagnostics');
      const diagnostics = await readProblemsForFile(preview, root);
      if (diagnostics === null) {
        markProblemsUnavailable(ticket, DESKTOP_ONLY);
        return;
      }
      filesRead += 1;
      for (const diagnostic of diagnostics) {
        const row = problemRowFromDiagnostic(diagnostic, root, file.path);
        if (row) rows.push(row);
      }
    } catch (error) {
      // One file failing is not a reason to show nothing for the others; the
      // failure is only reported if it is the only thing that happened.
      lastError = describeError(error);
    }
  }

  if (filesRead === 0 && lastError) {
    failProblemsLoad(ticket, `Could not read problems: ${lastError}`);
    return;
  }
  applyProblems(ticket, rows, 'open-files', filesRead);
}

/** Backend diagnostics → rows, dropping any that say nothing about a file. */
function rowsFrom(
  diagnostics: Parameters<typeof problemRowFromDiagnostic>[0][],
  root: string
): ProblemRow[] {
  const rows: ProblemRow[] = [];
  for (const diagnostic of diagnostics) {
    const row = problemRowFromDiagnostic(diagnostic, root);
    if (row) rows.push(row);
  }
  return rows;
}
