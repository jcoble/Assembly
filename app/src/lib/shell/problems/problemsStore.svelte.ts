/**
 * problemsStore.svelte.ts — Svelte 5 runes state for the /next Problems panel.
 *
 * Holds the STATE of the panel — the problems the language server has reported,
 * which project they belong to, which route answered, and the text typed into
 * the filter box — plus the pure rules that turn a pile of diagnostics into
 * something a person can read: grouped by file, worst first, counted in words.
 *
 * Two rules this module exists to enforce (same shape as
 * `context/contextStore.svelte.ts`):
 *
 * 1. **No backend, ever.** Nothing here calls Tauri, fetches, or touches
 *    browser storage. Every backend call lives in `problemsService.ts` (which
 *    reaches the desktop through `problemsBackend.ts`) and lands here as a
 *    plain mutation.
 * 2. **No `$effect`.** `$effect` is illegal in a `.svelte.ts` module and against
 *    the constitution. Loading is driven imperatively by the service, and only
 *    when the panel is opened or refreshed — never at launch.
 *
 * Each load takes a monotonic ticket from `beginProblemsLoad` and hands it back
 * when it finishes; a result whose ticket is no longer the current one is
 * dropped, so a slow first read can never overwrite a fast second one.
 *
 * The one import is type-only on purpose: the module stays runnable on its own,
 * which is what `scripts/problemsStore.test.mjs` compiles and exercises.
 */
import type { SourceDiagnostic, SourceDiagnosticSeverity } from '../../sourceData.ts';

/**
 * A diagnostic as the desktop returns it. The backend's whole-project command
 * fills in `path`; the per-file command (which is asked about one file at a
 * time) may leave it out, and the caller supplies the file it asked about.
 */
export interface RawProblemDiagnostic extends SourceDiagnostic {
  path?: string | null;
}

/** One problem, ready to draw: which file, where in it, and what it says. */
export interface ProblemRow {
  /** Absolute path of the file the problem is in. */
  path: string;
  /** The same path, shortened against the project folder, for the screen. */
  relativePath: string;
  fileName: string;
  severity: SourceDiagnosticSeverity;
  message: string;
  /** 1-based. */
  line: number;
  /** 1-based. */
  column: number;
  /** Which tool said so (`tsserver`, `rust-analyzer`, …), when it said. */
  source?: string;
}

/** How many problems of each kind, plus how many altogether. */
export interface ProblemCounts {
  error: number;
  warning: number;
  info: number;
  hint: number;
  total: number;
}

/** Every problem in one file, worst first. */
export interface ProblemFileGroup {
  path: string;
  relativePath: string;
  fileName: string;
  rows: ProblemRow[];
  counts: ProblemCounts;
  /** The worst thing wrong with this file — what its badge shows. */
  worstSeverity: SourceDiagnosticSeverity | null;
}

/**
 * Which route answered:
 *  - `workspace` — the desktop listed everything the language server has
 *    reported for the whole project;
 *  - `open-files` — the desktop is an older build with no whole-project
 *    command, so the panel asked file by file about the files you have open.
 */
export type ProblemsSource = 'workspace' | 'open-files';

/** Why the list is empty. Exactly one of these is true at a time. */
export type ProblemsEmptyKind =
  | 'not-loaded'
  | 'no-project'
  | 'desktop-only'
  | 'load-failed'
  | 'no-open-files'
  | 'nothing-reported-yet'
  | 'clean'
  | 'no-match';

/** The fields `problemsEmptyKind` needs. The live state satisfies it. */
export interface ProblemsSnapshot {
  activated: boolean;
  loading: boolean;
  root: string | null;
  rows: readonly ProblemRow[];
  filter: string;
  error: string | null;
  unavailableReason: string | null;
  source: ProblemsSource | null;
  filesConsidered: number;
}

// ── The state ─────────────────────────────────────────────────────────────────

/**
 * The single reactive problems state. Read fields directly in components
 * (`problemsState.rows`); mutate only through the functions below.
 */
export const problemsState = $state<{
  /** The panel has been opened at least once and asked for a load. */
  activated: boolean;
  /** Project folder the problems belong to; `null` when no session is picked. */
  root: string | null;
  /** Every problem, unsorted and unfiltered — the panel does both on the way out. */
  rows: ProblemRow[];
  /** A load is in flight. */
  loading: boolean;
  /** Why the last load failed, in plain English. `null` when it did not. */
  error: string | null;
  /**
   * Why there is nothing to show through no fault of anyone — for example the
   * language server only runs in the desktop app. Non-null means "do not show
   * an empty list, show this sentence".
   */
  unavailableReason: string | null;
  /** Which route answered the last successful load. */
  source: ProblemsSource | null;
  /**
   * How many files the last load actually looked at. Only the file-by-file
   * route can know this; the whole-project route reports 0 and the panel says
   * so instead of guessing.
   */
  filesConsidered: number;
  /** What the user typed into the filter box, exactly as typed. */
  filter: string;
  /** Monotonic ticket of the most recent load request. */
  requestId: number;
  /** `Date.now()` of the last successful load, or `null`. */
  loadedAt: number | null;
}>({
  activated: false,
  root: null,
  rows: [],
  loading: false,
  error: null,
  unavailableReason: null,
  source: null,
  filesConsidered: 0,
  filter: '',
  requestId: 0,
  loadedAt: null
});

// ── Mutations ─────────────────────────────────────────────────────────────────

/** Record that the panel has been opened and its first load has begun. */
export function markProblemsActivated(): void {
  problemsState.activated = true;
}

/** Point the panel at a project folder. Pure bookkeeping — it starts no load. */
export function setProblemsRoot(root: string | null): void {
  problemsState.root = root && root.trim().length > 0 ? root : null;
}

/**
 * Take a ticket for a new load: marks the panel loading and clears the last
 * error. Pass the ticket back to `applyProblems` / `failProblemsLoad` /
 * `markProblemsUnavailable` — a stale ticket is ignored.
 */
export function beginProblemsLoad(): number {
  problemsState.requestId += 1;
  problemsState.loading = true;
  problemsState.error = null;
  problemsState.unavailableReason = null;
  return problemsState.requestId;
}

/** `true` while `requestId` is still the newest ticket handed out. */
export function isCurrentProblemsRequest(requestId: number): boolean {
  return problemsState.requestId === requestId;
}

/** Land a successful load. Returns `false` when a newer load superseded it. */
export function applyProblems(
  requestId: number,
  rows: ProblemRow[],
  source: ProblemsSource,
  filesConsidered: number
): boolean {
  if (problemsState.requestId !== requestId) return false;
  problemsState.rows = rows;
  problemsState.source = source;
  problemsState.filesConsidered = filesConsidered;
  problemsState.loading = false;
  problemsState.error = null;
  problemsState.unavailableReason = null;
  problemsState.loadedAt = Date.now();
  return true;
}

/** Land a failed load. Returns `false` when a newer load superseded it. */
export function failProblemsLoad(requestId: number, message: string): boolean {
  if (problemsState.requestId !== requestId) return false;
  problemsState.loading = false;
  problemsState.error = message;
  return true;
}

/**
 * Land a load that could not run at all — nothing is wrong, the data just is
 * not reachable here. Returns `false` when a newer load superseded it.
 */
export function markProblemsUnavailable(requestId: number, reason: string): boolean {
  if (problemsState.requestId !== requestId) return false;
  problemsState.rows = [];
  problemsState.loading = false;
  problemsState.error = null;
  problemsState.unavailableReason = reason;
  return true;
}

/** Remember what the user typed into the filter box, exactly as typed. */
export function setProblemsFilter(filter: string): void {
  problemsState.filter = filter;
}

/** Drop everything back to launch state. Used by tests and by a full reset. */
export function resetProblems(): void {
  problemsState.activated = false;
  problemsState.root = null;
  problemsState.rows = [];
  problemsState.loading = false;
  problemsState.error = null;
  problemsState.unavailableReason = null;
  problemsState.source = null;
  problemsState.filesConsidered = 0;
  problemsState.filter = '';
  problemsState.requestId = 0;
  problemsState.loadedAt = null;
}

// ── Severity vocabulary ───────────────────────────────────────────────────────

const SEVERITY_ORDER: SourceDiagnosticSeverity[] = ['error', 'warning', 'info', 'hint'];

/** Sort position of a severity: errors first (0), hints last, junk after that. */
export function severityRank(severity: string): number {
  const rank = SEVERITY_ORDER.indexOf(severity as SourceDiagnosticSeverity);
  return rank === -1 ? SEVERITY_ORDER.length : rank;
}

/**
 * What a severity is called on screen. An `info` diagnostic is a "note" — the
 * word people use for it — and nothing here ever shows a severity number.
 */
export function severityWord(severity: SourceDiagnosticSeverity, count = 1): string {
  const singular =
    severity === 'error'
      ? 'error'
      : severity === 'warning'
        ? 'warning'
        : severity === 'info'
          ? 'note'
          : 'hint';
  return count === 1 ? singular : `${singular}s`;
}

// ── Paths ─────────────────────────────────────────────────────────────────────

/** Last part of a path — `/a/b/app.ts` → `app.ts`. */
export function fileNameOf(path: string): string {
  const parts = path.split('/').filter(Boolean);
  return parts[parts.length - 1] ?? path;
}

/**
 * A path shortened against the project folder. A file outside the project keeps
 * its whole path, because a made-up short name would point at the wrong file.
 */
export function relativeToRoot(path: string, root: string | null): string {
  const base = (root ?? '').replace(/\/+$/, '');
  if (!base) return path;
  if (path === base) return fileNameOf(path);
  return path.startsWith(`${base}/`) ? path.slice(base.length + 1) : path;
}

/** `file:///a/b.ts` → `/a/b.ts`; anything else is handed back unchanged. */
function pathFromUri(value: string): string {
  if (!value.startsWith('file://')) return value;
  const withoutScheme = value.slice('file://'.length);
  try {
    return decodeURIComponent(withoutScheme);
  } catch {
    return withoutScheme;
  }
}

// ── Building rows ─────────────────────────────────────────────────────────────

/**
 * Turn one backend diagnostic into a row. `fallbackPath` is the file the
 * diagnostic was asked about, used when the diagnostic does not carry its own
 * path. A diagnostic with no path either way is dropped (`null`) rather than
 * shown as belonging to nowhere.
 */
export function problemRowFromDiagnostic(
  diagnostic: RawProblemDiagnostic,
  root: string | null,
  fallbackPath?: string
): ProblemRow | null {
  const raw = (diagnostic.path ?? fallbackPath ?? '').trim();
  if (!raw) return null;
  const path = pathFromUri(raw);
  return {
    path,
    relativePath: relativeToRoot(path, root),
    fileName: fileNameOf(path),
    severity: diagnostic.severity,
    message: diagnostic.message,
    line: Math.max(1, diagnostic.line),
    column: Math.max(1, diagnostic.column),
    source: diagnostic.source
  };
}

/** A stable key for one row, so a re-render does not shuffle the list. */
export function problemRowKey(row: ProblemRow): string {
  return `${row.path}:${row.line}:${row.column}:${row.severity}:${row.message}`;
}

// ── Counting ──────────────────────────────────────────────────────────────────

/** Zero of everything. */
export function emptyProblemCounts(): ProblemCounts {
  return { error: 0, warning: 0, info: 0, hint: 0, total: 0 };
}

/** How many problems of each severity are in `rows`. */
export function countProblems(rows: readonly ProblemRow[]): ProblemCounts {
  const counts = emptyProblemCounts();
  for (const row of rows) {
    if (row.severity === 'error') counts.error += 1;
    else if (row.severity === 'warning') counts.warning += 1;
    else if (row.severity === 'info') counts.info += 1;
    else if (row.severity === 'hint') counts.hint += 1;
    counts.total += 1;
  }
  return counts;
}

/** "2 errors, 1 warning, 1 note" — or "No problems" when there are none. */
export function describeProblemCounts(counts: ProblemCounts): string {
  const parts: string[] = [];
  for (const severity of SEVERITY_ORDER) {
    const value = counts[severity];
    if (value > 0) parts.push(`${value} ${severityWord(severity, value)}`);
  }
  return parts.length === 0 ? 'No problems' : parts.join(', ');
}

/** The worst severity present, or `null` when the list is empty. */
export function worstSeverity(rows: readonly ProblemRow[]): SourceDiagnosticSeverity | null {
  let worst: SourceDiagnosticSeverity | null = null;
  for (const row of rows) {
    if (worst === null || severityRank(row.severity) < severityRank(worst)) worst = row.severity;
  }
  return worst;
}

// ── Sorting, grouping, filtering ──────────────────────────────────────────────

/** Errors first, then by line, then by column. Returns a new array. */
export function sortProblemRows(rows: readonly ProblemRow[]): ProblemRow[] {
  return [...rows].sort((left, right) => {
    const bySeverity = severityRank(left.severity) - severityRank(right.severity);
    if (bySeverity !== 0) return bySeverity;
    if (left.line !== right.line) return left.line - right.line;
    return left.column - right.column;
  });
}

/**
 * Group problems by the file they are in.
 *
 * File order: the worst file first (an error beats a warning), then the file
 * with more of that worst kind, then by name so the list never reshuffles for
 * no reason. Inside a file, rows are sorted the same way as anywhere else.
 */
export function groupProblemsByFile(rows: readonly ProblemRow[]): ProblemFileGroup[] {
  const byPath = new Map<string, ProblemRow[]>();
  for (const row of rows) {
    const existing = byPath.get(row.path);
    if (existing) existing.push(row);
    else byPath.set(row.path, [row]);
  }

  const groups: ProblemFileGroup[] = [];
  for (const [path, fileRows] of byPath) {
    const sorted = sortProblemRows(fileRows);
    const first = sorted[0];
    groups.push({
      path,
      relativePath: first?.relativePath ?? path,
      fileName: first?.fileName ?? fileNameOf(path),
      rows: sorted,
      counts: countProblems(sorted),
      worstSeverity: worstSeverity(sorted)
    });
  }

  return groups.sort((left, right) => {
    const leftWorst = left.worstSeverity;
    const rightWorst = right.worstSeverity;
    const bySeverity =
      severityRank(leftWorst ?? 'hint') - severityRank(rightWorst ?? 'hint');
    if (bySeverity !== 0) return bySeverity;
    if (leftWorst && rightWorst && leftWorst === rightWorst) {
      const byCount = right.counts[rightWorst] - left.counts[leftWorst];
      if (byCount !== 0) return byCount;
    }
    return left.relativePath.localeCompare(right.relativePath);
  });
}

/**
 * Keep the rows that match what was typed. A match may be anywhere in the
 * message, the file name, the path, the tool that reported it, or the severity
 * word — so typing "error" narrows the list to errors without a second control.
 */
export function filterProblemRows(rows: readonly ProblemRow[], filter: string): ProblemRow[] {
  const needle = filter.trim().toLowerCase();
  if (!needle) return [...rows];
  return rows.filter((row) => {
    const words = [
      row.message,
      row.fileName,
      row.relativePath,
      row.path,
      row.source ?? '',
      severityWord(row.severity, 1),
      severityWord(row.severity, 2)
    ];
    return words.some((word) => word.toLowerCase().includes(needle));
  });
}

// ── What the panel says ───────────────────────────────────────────────────────

/**
 * The sentence under the header: where these problems came from. The
 * file-by-file route says out loud that it is not the whole project, because a
 * short list there means "we only looked at these files", not "your project is
 * nearly clean".
 */
export function describeProblemsSource(
  source: ProblemsSource | null,
  filesConsidered: number
): string {
  if (source === 'workspace') {
    return 'These are every problem the language server has reported for this project.';
  }
  if (source === 'open-files') {
    const files = `${filesConsidered} ${filesConsidered === 1 ? 'file' : 'files'}`;
    return `This desktop app cannot list problems for a whole project yet, so these come from the ${files} you have open.`;
  }
  return '';
}

/**
 * Which empty state the panel is in, or `null` when it has rows to show.
 * Ordered so the most specific reason wins: a filter that hides everything is
 * about the filter, not about the project.
 */
export function problemsEmptyKind(snapshot: ProblemsSnapshot): ProblemsEmptyKind | null {
  if (snapshot.rows.length > 0) {
    return filterProblemRows(snapshot.rows, snapshot.filter).length > 0 ? null : 'no-match';
  }
  if (!snapshot.activated) return 'not-loaded';
  if (snapshot.unavailableReason) return 'desktop-only';
  if (snapshot.error) return 'load-failed';
  if (!snapshot.root) return 'no-project';
  if (snapshot.source === 'open-files') {
    return snapshot.filesConsidered === 0 ? 'no-open-files' : 'clean';
  }
  if (snapshot.source === 'workspace') return 'nothing-reported-yet';
  return 'not-loaded';
}

/** The two lines an empty panel shows: a headline, and a sentence under it. */
export function describeProblemsEmptyState(kind: ProblemsEmptyKind): {
  headline: string;
  hint: string;
} {
  switch (kind) {
    case 'not-loaded':
      return {
        headline: 'Nothing loaded yet',
        hint: 'Press Refresh to ask the language server what is wrong with this project.'
      };
    case 'no-project':
      return {
        headline: 'No project picked',
        hint: 'Pick a session on the left and its problems show up here.'
      };
    case 'desktop-only':
      return {
        headline: 'Problems need the desktop app',
        hint: 'The language server runs inside the desktop app, so there is nothing to read here in a browser.'
      };
    case 'load-failed':
      return {
        headline: 'Could not read the problems',
        hint: 'The message above says what went wrong. Press Refresh to try again.'
      };
    case 'no-open-files':
      return {
        headline: 'No files to look at',
        hint: 'This desktop app can only report problems for files you have open, so open a file and refresh.'
      };
    case 'nothing-reported-yet':
      return {
        headline: 'No problems found',
        hint: 'Nothing has been reported for this project. If the language server is still starting, press Refresh in a moment.'
      };
    case 'clean':
      return {
        headline: 'No problems found',
        hint: 'The files you have open are clean.'
      };
    case 'no-match':
      return {
        headline: 'Nothing matches that',
        hint: 'Clear the filter box to see every problem again.'
      };
  }
}
