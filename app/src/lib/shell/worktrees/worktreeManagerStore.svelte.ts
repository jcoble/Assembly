/**
 * worktreeManagerStore.svelte.ts — runes state for the worktree manager pane.
 *
 * Same two rules as every other /next store:
 *
 * 1. **No backend, ever.** Nothing here invokes, fetches or reads storage.
 *    Every desktop call lives in `worktreeManagerService.ts` and lands here as a
 *    plain assignment.
 * 2. **No `$effect`.** `$effect` is illegal in a `.svelte.ts` module. Nothing in
 *    here reacts; it is a value bag with mutators.
 *
 * It holds the three lists RAW — worktrees, repository summaries, sessions —
 * and never the joined rows. The join is a pure function
 * (`worktreeManagerRows.ts`) the component runs inside a `$derived`, so there is
 * exactly one copy of the answer and it can never go stale behind the data.
 *
 * Every import is type-only on purpose, which keeps the module compilable on its
 * own the way `contextStore.svelte.ts` is.
 *
 * Nothing here is persisted, so no mutator has a `persist()` to call: what the
 * pane shows is read fresh from the machine each time it is opened, and a filter
 * box or an open detail row is not worth remembering across a restart.
 */
import type { GitRepositorySummary, ProjectWorktree } from '../../tauriSource.ts';
import type { WorktreeSessionInput } from './worktreeManagerRows.ts';

/** Which action is running, or '' when none is. */
export type WorktreeActionKind = '' | 'remove' | 'clear' | 'force-remove' | 'archive';

/**
 * Whether this build of the desktop app can do a particular thing.
 * `'unknown'` is the state before the app has been asked, and it is real screen
 * time: the question is asked once when the pane first opens. Nothing pretends
 * to know the answer while it is still `'unknown'`.
 */
export type BackendSupport = 'unknown' | 'available' | 'unavailable';

/**
 * Whether this build of the desktop app can force-remove a worktree. The
 * destructive button stays switched off while this is `'unknown'`, because
 * offering a button that might quietly do something gentler instead is worse
 * than offering nothing.
 */
export type ForceRemoveSupport = BackendSupport;

export interface WorktreeManagerState {
  /** The pane has been shown and pointed at a folder at least once. */
  activated: boolean;
  /** The folder the worktrees were read for, or `null` before there is one. */
  root: string | null;
  /** Name of the project that folder belongs to, for the empty sentences. */
  projectName: string;

  // ── what was read ──────────────────────────────────────────────────────────
  worktrees: ProjectWorktree[];
  /** Repository summaries — the only place ahead/behind counts come from. */
  repositories: GitRepositorySummary[];
  /** The shell's own sessions, so a row can say who worked in that folder. */
  sessions: WorktreeSessionInput[];

  // ── how the read went ──────────────────────────────────────────────────────
  loading: boolean;
  /** Why the last read failed, in plain English. '' when it did not. */
  error: string;
  /**
   * Why there is nothing to show through nobody's fault — the data only exists
   * in the desktop app, or no project is selected yet. Non-empty means "show
   * this sentence instead of an empty list".
   */
  unavailableReason: string;
  /** Monotonic ticket of the newest read, so a slow one cannot overwrite it. */
  requestId: number;
  loadedAt: number | null;

  // ── what the user is doing ─────────────────────────────────────────────────
  /** Path of the row whose details are open, or '' when none is. */
  selectedPath: string;
  /** What is typed in the filter box. */
  filter: string;
  /** Path of the worktree an action is running on, or ''. */
  busyPath: string;
  busyAction: WorktreeActionKind;
  /** What the last finished action did, in its own words from the desktop app. */
  actionMessage: string;
  /** Why the last action did not happen. '' when it did. */
  actionError: string;

  // ── what the desktop app can do ────────────────────────────────────────────
  forceRemoveSupport: ForceRemoveSupport;
  /**
   * Whether clearing one folder-gone row clears only that row. When this is not
   * `'available'`, the confirmation dialog says out loud that every other row
   * whose folder is gone goes with it — see `describeRemovalQuestion`.
   */
  pruneSingleRowSupport: BackendSupport;
}

/** Shown on the destructive button while this build cannot actually force. */
export const FORCE_REMOVE_UNAVAILABLE_TOOLTIP =
  'This build of the app cannot do this yet — restart the desktop app after updating.';

export function createWorktreeManagerState(): WorktreeManagerState {
  return {
    activated: false,
    root: null,
    projectName: '',
    worktrees: [],
    repositories: [],
    sessions: [],
    loading: false,
    error: '',
    unavailableReason: '',
    requestId: 0,
    loadedAt: null,
    selectedPath: '',
    filter: '',
    busyPath: '',
    busyAction: '',
    actionMessage: '',
    actionError: '',
    forceRemoveSupport: 'unknown',
    pruneSingleRowSupport: 'unknown'
  };
}

/** The reactive state the pane reads and the service writes. */
export const worktreeManager = $state<WorktreeManagerState>(createWorktreeManagerState());

// ── mutations ────────────────────────────────────────────────────────────────

/** Point the pane at a folder. Pure bookkeeping — it starts no read. */
export function setWorktreeManagerInput(input: {
  root: string | null;
  projectName?: string;
  sessions: WorktreeSessionInput[];
}): void {
  worktreeManager.activated = true;
  const changedFolder = worktreeManager.root !== input.root;
  worktreeManager.root = input.root;
  worktreeManager.projectName = input.projectName?.trim() ?? '';
  worktreeManager.sessions = input.sessions;
  if (!changedFolder) return;
  // A different repository has different rows; keeping the old selection would
  // open a detail panel for a folder that is not on screen any more.
  worktreeManager.selectedPath = '';
  worktreeManager.actionMessage = '';
  worktreeManager.actionError = '';
}

/** Take a ticket for a new read. Hand it back to whichever landing it reaches. */
export function beginWorktreeLoad(): number {
  worktreeManager.requestId += 1;
  worktreeManager.loading = true;
  worktreeManager.error = '';
  worktreeManager.unavailableReason = '';
  return worktreeManager.requestId;
}

/** Land a successful read. `false` when a newer read superseded it. */
export function applyWorktreeLoad(
  requestId: number,
  worktrees: ProjectWorktree[],
  repositories: GitRepositorySummary[]
): boolean {
  if (worktreeManager.requestId !== requestId) return false;
  worktreeManager.worktrees = worktrees;
  worktreeManager.repositories = repositories;
  worktreeManager.loading = false;
  worktreeManager.error = '';
  worktreeManager.unavailableReason = '';
  worktreeManager.loadedAt = Date.now();
  return true;
}

/** Land a failed read. `false` when a newer read superseded it. */
export function failWorktreeLoad(requestId: number, message: string): boolean {
  if (worktreeManager.requestId !== requestId) return false;
  worktreeManager.loading = false;
  worktreeManager.error = message;
  return true;
}

/** Land a read that could not run at all, and say plainly why. */
export function markWorktreesUnavailable(requestId: number, reason: string): boolean {
  if (worktreeManager.requestId !== requestId) return false;
  worktreeManager.worktrees = [];
  worktreeManager.repositories = [];
  worktreeManager.loading = false;
  worktreeManager.error = '';
  worktreeManager.unavailableReason = reason;
  return true;
}

/** Record what this build of the desktop app can do. */
export function setForceRemoveSupport(support: ForceRemoveSupport): void {
  worktreeManager.forceRemoveSupport = support;
}

/** Record whether clearing one folder-gone row leaves the other ones alone. */
export function setPruneSingleRowSupport(support: BackendSupport): void {
  worktreeManager.pruneSingleRowSupport = support;
}

/** Open one row's details, or close them by passing the open one again. */
export function toggleWorktreeDetail(path: string): void {
  worktreeManager.selectedPath = worktreeManager.selectedPath === path ? '' : path;
}

/** An action is starting on one worktree. Clears the last one's words. */
export function beginWorktreeAction(path: string, action: WorktreeActionKind): void {
  worktreeManager.busyPath = path;
  worktreeManager.busyAction = action;
  worktreeManager.actionMessage = '';
  worktreeManager.actionError = '';
}

/** An action finished, and the desktop app said this about it. */
export function finishWorktreeAction(message: string, worktrees?: ProjectWorktree[]): void {
  worktreeManager.busyPath = '';
  worktreeManager.busyAction = '';
  worktreeManager.actionMessage = message;
  worktreeManager.actionError = '';
  // Every action command answers with the list as it now stands, so the pane
  // never has to guess what changed or read the machine a second time.
  if (worktrees) worktreeManager.worktrees = worktrees;
}

/** An action did not happen, for this reason. */
export function failWorktreeAction(message: string): void {
  worktreeManager.busyPath = '';
  worktreeManager.busyAction = '';
  worktreeManager.actionError = message;
}

/** Drop everything back to launch state. For tests and a full reset. */
export function resetWorktreeManager(): void {
  Object.assign(worktreeManager, createWorktreeManagerState());
}
