/**
 * gitService.ts — every backend call the /next source-control panel makes.
 *
 * Constitution rules this file exists to keep:
 *
 * - **No `$effect` anywhere.** Nothing here reacts to state. The panel's UI
 *   events and the integrator's first-activation hook call these functions
 *   imperatively; that is the only way a git command ever runs.
 * - **Nothing loads at import.** Building the service allocates an object and
 *   touches no backend. The first call happens in `activate(root)`, which the
 *   integrator invokes when the panel is first shown.
 * - **Every backend call is counted.** `countInvoke('<command name>')` runs
 *   immediately before each one, so the dev invoke counter tells the truth about
 *   what this panel costs.
 * - **No stand-in data.** The `…FromTauri` wrappers return `null` when the app
 *   is not running under Tauri. That flips the panel to a plain "desktop app
 *   only" state; it never fabricates a repository.
 *
 * Superseded-request handling is the shape the old shell used
 * (`src/routes/+page.svelte` around :3776, `loadProjectGitStatus` +
 * `isCurrentProjectGitStatusRequest`): a monotonic request id per lane of work,
 * re-checked after every await, plus a check that the repository has not changed
 * underneath. A superseded read drops its result silently and does NOT clear the
 * loading flag the newer read owns.
 */

import {
  bridgeGitBackend,
  canChangeRepository,
  hasGitBridge,
  withGitDiffTimeout
} from './gitBackendExtra.ts';
import { countInvoke } from '../devInvokeCounter.svelte.ts';
import { setGitSurfaceDiagnostics } from '../resourceDiagnostics.svelte.ts';
import { parseRemoteWorkspacePath } from '../../workspacePaths.ts';
import { rail } from '../stores/sessionRailStore.svelte.ts';
import { createGitAmend } from './gitAmend.ts';
import {
  addGitRemoteFromTauri,
  amendGitCommitFromTauri,
  commitGitRepositoryFromTauri,
  createGitBranchFromTauri,
  discardAllGitChangesFromTauri,
  discardGitPathsFromTauri,
  fetchGitRepositoryFromTauri,
  initGitRepositoryFromTauri,
  listGitBranchesFromTauri,
  listGitStashesFromTauri,
  popGitStashFromTauri,
  stashGitChangesFromTauri,
  switchGitBranchFromTauri,
  pullGitRepositoryFromTauri,
  pushGitRepositoryFromTauri,
  publishGitRepositoryFromTauri,
  readGitCommitHistoryFromTauri,
  readProjectGitStatusFromTauri,
  readSourceGitDiffFromTauri,
  stageGitPathsFromTauri,
  unstageGitPathsFromTauri,
  type GitActionResult,
  type GitBranchList,
  type GitHistoryPage,
  type GitStashEntry,
  type ProjectGitFileStatus,
  type ProjectGitStatus,
  type SourceGitDiff
} from '../../tauriSource.ts';
import {
  clearSelectedGitFile,
  describeGitError,
  gitPanel,
  resetGitPanelState,
  type GitActionKind,
  type GitPanelState,
  type GitSurfaceOwner
} from './gitPanelStore.svelte.ts';
import {
  gitCommitFiles,
  gitCommitFilesView,
  resetGitCommitFilesView
} from './gitCommitFilesStore.svelte.ts';

/** Fixed commit count for the first and every later cursor page. */
export const COMMIT_HISTORY_LIMIT = 24;

/** Shown whenever a wrapper returns `null` — i.e. we are not in the desktop app. */
export const DESKTOP_ONLY_MESSAGE =
  'Source control runs in the desktop app only. Nothing is loaded here.';

/**
 * Every git command this panel can issue. Named as an interface so the node
 * test can drive the service with a stub and prove the superseded-request
 * handling, exactly as `terminalService.ts` does with its terminal backend.
 */
export interface GitBackend {
  readStatus(root: string): Promise<ProjectGitStatus | null>;
  /** Throw away the named files' changes. Deletes untracked files. */
  discard(root: string, paths: string[]): Promise<GitActionResult | null>;
  /** Throw away the whole working copy. */
  discardAll(root: string, includeUntracked: boolean): Promise<GitActionResult | null>;
  listBranches(root: string, signal?: AbortSignal): Promise<GitBranchList | null>;
  createBranch(root: string, name: string, checkout: boolean): Promise<GitActionResult | null>;
  switchBranch(root: string, name: string): Promise<GitActionResult | null>;
  stash(root: string, includeUntracked: boolean, message: string): Promise<GitActionResult | null>;
  popStash(root: string, index: number | null): Promise<GitActionResult | null>;
  listStashes(root: string): Promise<GitStashEntry[] | null>;
  amend(root: string, message: string): Promise<GitActionResult | null>;
  readDiff(root: string, absolutePath: string, signal?: AbortSignal): Promise<SourceGitDiff | null>;
  readHistory(
    root: string,
    cursor?: string | null,
    relativePath?: string | null
  ): Promise<GitHistoryPage | null>;
  stage(root: string, paths: string[]): Promise<GitActionResult | null>;
  unstage(root: string, paths: string[]): Promise<GitActionResult | null>;
  commit(root: string, message: string): Promise<GitActionResult | null>;
  fetch(root: string): Promise<GitActionResult | null>;
  pull(root: string): Promise<GitActionResult | null>;
  push(root: string): Promise<GitActionResult | null>;
  addRemote(root: string, name: string, url: string): Promise<GitActionResult | null>;
  publish(root: string, expectedBranch: string, remote: string): Promise<GitActionResult | null>;
  /** Make a folder with no repository one. */
  initRepository(root: string, signal: AbortSignal): Promise<void>;
}

/**
 * The real backend: the existing `tauriSource.ts` wrappers, each preceded by the
 * invoke count for the Tauri command it runs.
 */
export function tauriGitBackend(count: (command: string) => void = countInvoke): GitBackend {
  return {
    readStatus(root) {
      count('project_git_status');
      return readProjectGitStatusFromTauri(root);
    },
    discard(root, paths) {
      count('discard_git_paths');
      return discardGitPathsFromTauri(root, paths);
    },
    discardAll(root, includeUntracked) {
      count('discard_all_git_changes');
      return discardAllGitChangesFromTauri(root, includeUntracked);
    },
    listBranches(root, signal) {
      count('list_git_branches');
      return listGitBranchesFromTauri(root, signal);
    },
    createBranch(root, name, checkout) {
      count('create_git_branch');
      return createGitBranchFromTauri(root, name, checkout);
    },
    switchBranch(root, name) {
      count('switch_git_branch');
      return switchGitBranchFromTauri(root, name);
    },
    stash(root, includeUntracked, message) {
      count('stash_git_changes');
      return stashGitChangesFromTauri(root, includeUntracked, message);
    },
    popStash(root, index) {
      count('pop_git_stash');
      return popGitStashFromTauri(root, index);
    },
    listStashes(root) {
      count('list_git_stashes');
      return listGitStashesFromTauri(root);
    },
    amend(root, message) {
      count('amend_git_commit');
      return amendGitCommitFromTauri(root, message);
    },
    readDiff(root, absolutePath, signal) {
      count('read_source_git_diff');
      return readSourceGitDiffFromTauri(root, absolutePath, signal);
    },
    readHistory(root, cursor, relativePath) {
      count('read_git_commit_history');
      return readGitCommitHistoryFromTauri(root, cursor, relativePath);
    },
    stage(root, paths) {
      count('stage_git_paths');
      return stageGitPathsFromTauri(root, paths);
    },
    unstage(root, paths) {
      count('unstage_git_paths');
      return unstageGitPathsFromTauri(root, paths);
    },
    commit(root, message) {
      count('commit_git_repository');
      return commitGitRepositoryFromTauri(root, message);
    },
    fetch(root) {
      count('fetch_git_repository');
      return fetchGitRepositoryFromTauri(root);
    },
    pull(root) {
      count('pull_git_repository');
      return pullGitRepositoryFromTauri(root);
    },
    push(root) {
      count('push_git_repository');
      return pushGitRepositoryFromTauri(root);
    },
    addRemote(root, name, url) {
      count('add_git_remote');
      return addGitRemoteFromTauri(root, name, url);
    },
    publish(root, expectedBranch, remote) {
      count('publish_git_repository');
      return publishGitRepositoryFromTauri(root, expectedBranch, remote);
    },
    initRepository(root, signal) {
      count('workspace_git_init');
      return initGitRepositoryFromTauri(root, signal);
    }
  };
}

export interface RequestGuard {
  /** Claim the newest request id; every older one is now superseded. */
  next(): number;
  /** Is `id` still the newest claim? */
  isCurrent(id: number): boolean;
  /** Supersede everything in flight without claiming a new id. */
  invalidate(): void;
}

/** A monotonic request id, so a slow answer can never overwrite a fast newer one. */
export function createRequestGuard(): RequestGuard {
  let latest = 0;
  return {
    next: () => (latest += 1),
    isCurrent: (id: number) => id === latest,
    invalidate: () => {
      latest += 1;
    }
  };
}

/**
 * Join a repository root and a repository-relative path. The diff command wants
 * an absolute path on disk; git status hands us a relative one.
 */
export function absolutePathWithin(root: string, relativePath: string): string {
  const base = root.replace(/\/+$/, '');
  const tail = relativePath.replace(/^\/+/, '');
  return tail === '' ? base : `${base}/${tail}`;
}

export interface GitService {
  /** The state this service writes. The panel reads the same object. */
  readonly state: GitPanelState;
  /**
   * Point the panel at a repository. Status is loaded by the visible Source
   * Control surface; history is loaded by a visible graph surface. Idempotent:
   * calling it again with the same root does nothing, so the integrator can call
   * it on every activation. A different root wipes the panel state.
   */
  activate(root: string | null): void;
  /** Re-read status and, when its surface is visible, commit history. */
  refresh(): Promise<void>;
  refreshStatus(): Promise<void>;
  /** Re-read the history at the size it has already grown to. */
  refreshHistory(): Promise<void>;
  /** Ask for another page of older commits. Does nothing once the list is whole. */
  loadMoreHistory(): Promise<void>;
  /** Read the first history page if a visible graph has no rows yet. A failed read waits for Refresh or another folder: callers run this on every effect pass. */
  ensureHistorySurface(): void;
  /** Release commit rows when no graph surface owns them. */
  releaseHistorySurface(): void;
  /** Show only commits which touched one repository-relative file. */
  showFileHistory(root: string, relativePath: string): Promise<void>;
  /** Return the history surface to the repository's complete history. */
  clearHistoryPath(): Promise<void>;
  /** Show this file's diff. */
  selectFile(file: ProjectGitFileStatus, owner?: GitSurfaceOwner): Promise<void>;
  /** Read one working-copy file's diff without selecting it. Null outside the
   * desktop app and the dev server, or when the panel has no repository. */
  readWorkingDiff(relativePath: string, signal: AbortSignal): Promise<SourceGitDiff | null>;
  /**
   * Put back a diff a session remembered, pointing the panel at that session's
   * repository first if it is somewhere else. The Diff tab is one tab for the
   * whole shell, so on a session switch what it shows must follow the session
   * in front — this is how a remembered diff comes back.
   */
  showStoredDiff(root: string, relativePath: string, owner?: GitSurfaceOwner): Promise<void>;
  /** Stop showing a diff. */
  clearSelection(owner?: GitSurfaceOwner | null): void;
  stagePaths(paths: string[]): Promise<void>;
  unstagePaths(paths: string[]): Promise<void>;
  /** Commit the staged changes using `state.commitMessage` unless one is given. */
  commit(message?: string): Promise<void>;
  setAmend(on: boolean): void;
  /** Rewrite the last commit with the staged changes, then turn Amend off. `setAmend(true)` fills an empty box with the last message. */
  amendCommit(message?: string): Promise<void>;
  /**
   * Throw away the named files' changes. THE CALLER MUST HAVE ASKED FIRST:
   * nothing in this service confirms anything, and git keeps no copy.
   */
  discardPaths(paths: string[]): Promise<void>;
  /** Throw away every change in the working copy. Same rule as above. */
  discardAll(includeUntracked: boolean): Promise<void>;
  /** The local branches, newest commit first. `null` outside the desktop app. */
  listBranches(signal?: AbortSignal): Promise<GitBranchList | null>;
  createBranch(name: string, checkout: boolean): Promise<void>;
  switchBranch(name: string): Promise<void>;
  /** Put the working copy aside. */
  stashChanges(includeUntracked: boolean, message: string): Promise<void>;
  /** Bring a stash back. `null` index means the most recent one. */
  popStash(index: number | null): Promise<void>;
  listStashes(): Promise<GitStashEntry[] | null>;
  runRemoteAction(action: 'fetch' | 'pull' | 'push'): Promise<void>;
  addRemote(expectedRoot: string, name: string, url: string): Promise<boolean>;
  publishBranch(expectedRoot: string, expectedBranch: string, remote: string): Promise<void>;
  /** Make the folder on screen a repository, then show it. */
  initRepository(signal: AbortSignal): Promise<void>;
}

export interface GitServiceOptions {
  backend?: GitBackend;
  state?: GitPanelState;
  diffTimeoutMs?: number;
}

export function createGitService(options: GitServiceOptions = {}): GitService {
  const backend =
    options.backend ??
    (canChangeRepository() || !hasGitBridge() ? tauriGitBackend() : bridgeGitBackend());
  const state = options.state ?? gitPanel;
  const diffTimeoutMs = options.diffTimeoutMs ?? 20_000;
  const statusGuard = createRequestGuard();
  const historyGuard = createRequestGuard();
  const diffGuard = createRequestGuard();
  /** Stops the selected diff's read when another file is picked, the selection
   * is cleared, or the repository changes. */
  let diffRead = new AbortController();
  let historySurfaceVisible = false;
  const amend = createGitAmend(state);
  let sourceControlSyncGeneration = 0;

  function publishGitDiagnostics(): void {
    setGitSurfaceDiagnostics(state.history.length, state.selectedDiff ? 1 : 0);
  }

  async function publishSourceControl(): Promise<void> {
    if (typeof window === 'undefined' || state !== gitPanel) return;
    const generation = ++sourceControlSyncGeneration;
    const snapshot = { root: state.root, status: state.status };
    const { syncRustGitSourceControl } = await import('../extensions/rustGitScmProvider.ts');
    if (generation !== sourceControlSyncGeneration) return;
    syncRustGitSourceControl(snapshot);
  }

  /** The repository is unchanged AND this request is still the newest one. */
  function stillCurrent(guard: RequestGuard, id: number, root: string): boolean {
    return guard.isCurrent(id) && state.root === root;
  }

  /** A `null` result means "not the desktop app": say so and load nothing. */
  function markDesktopOnly(): void {
    state.desktopOnly = true;
    state.status = null;
    state.history = [];
    state.historyRequested = 0;
    state.historyNextCursor = null;
    state.historyComplete = false;
    state.historyPaged = false;
    publishGitDiagnostics();
    void publishSourceControl();
  }

  async function refreshStatus(): Promise<void> {
    const root = state.root;
    if (!root) return;
    const id = statusGuard.next();
    state.statusLoading = true;
    state.statusError = '';

    try {
      const status = await backend.readStatus(root);
      if (!stillCurrent(statusGuard, id, root)) return;
      if (!status) {
        markDesktopOnly();
        return;
      }
      state.desktopOnly = false;
      state.status = status;
      void publishSourceControl();
    } catch (error) {
      if (!stillCurrent(statusGuard, id, root)) return;
      state.status = null;
      state.statusError = describeGitError(error, 'Could not read the repository status.', rail.remoteProfiles);
      void publishSourceControl();
    } finally {
      if (stillCurrent(statusGuard, id, root)) state.statusLoading = false;
    }
  }

  /**
   * Read one cursor page. A plain load replaces the graph; "Load more" appends
   * exactly the next page the backend returned a cursor for.
   */
  async function loadHistory(cursor: string | null, loadingMore: boolean): Promise<void> {
    const root = state.root;
    if (!root) return;
    const id = historyGuard.next();
    if (loadingMore) state.historyLoadingMore = true;
    else state.historyLoading = true;
    state.historyError = '';

    try {
      const page = await backend.readHistory(root, cursor, state.historyPath || null);
      if (!stillCurrent(historyGuard, id, root)) return;
      if (!page) {
        markDesktopOnly();
        return;
      }
      state.desktopOnly = false;
      if (loadingMore) {
        const loadedShas = new Set(state.history.map((commit) => commit.sha));
        state.history = [
          ...state.history,
          ...page.commits.filter((commit) => !loadedShas.has(commit.sha))
        ];
      } else {
        state.history = page.commits;
      }
      state.historyRequested = state.history.length;
      state.historyNextCursor = page.nextCursor;
      state.historyComplete = page.complete;
    } catch (error) {
      if (!stillCurrent(historyGuard, id, root)) return;
      // A failed "Load more" keeps what is already on screen; only a failed
      // refresh has nothing left to show.
      if (!loadingMore) state.history = [];
      state.historyError = describeGitError(error, 'Could not read the commit history.', rail.remoteProfiles);
    } finally {
      if (stillCurrent(historyGuard, id, root)) {
        if (loadingMore) state.historyLoadingMore = false;
        else state.historyLoading = false;
        publishGitDiagnostics();
      }
    }
  }

  /**
   * Re-read the first page. Refresh is a new visible graph projection, not a
   * reread of every older page the user had loaded before.
   */
  async function refreshHistory(): Promise<void> {
    if (!historySurfaceVisible) return;
    state.history = [];
    state.historyRequested = 0;
    state.historyNextCursor = null;
    state.historyComplete = false;
    state.historyPaged = false;
    await loadHistory(null, false);
  }

  async function loadMoreHistory(): Promise<void> {
    if (!historySurfaceVisible) return;
    if (!state.root || state.historyLoading || state.historyLoadingMore) return;
    if (state.historyComplete || state.historyNextCursor === null) return;
    state.historyPaged = true;
    await loadHistory(state.historyNextCursor, true);
  }

  function ensureHistorySurface(): void {
    historySurfaceVisible = true;
    if (!state.root || state.historyLoading || state.historyLoadingMore) return;
    if (state.history.length > 0 || state.historyComplete || state.historyError) return;
    void loadHistory(null, false);
  }

  function releaseHistorySurface(): void {
    historySurfaceVisible = false;
    historyGuard.invalidate();
    // A status read in flight still lands: the Changes tab may be waiting on
    // it, and status is already null, so the tab would never ask again.
    state.status = null;
    state.statusError = '';
    state.history = [];
    state.historyPath = '';
    state.historyLoading = false;
    state.historyError = '';
    state.historyRequested = 0;
    state.historyNextCursor = null;
    state.historyLoadingMore = false;
    state.historyComplete = false;
    state.historyPaged = false;
    publishGitDiagnostics();
    void publishSourceControl();
  }

  async function refresh(): Promise<void> {
    await Promise.all([refreshStatus(), refreshHistory()]);
  }

  async function showFileHistory(root: string, relativePath: string): Promise<void> {
    const targetRoot = root.trim();
    const targetPath = relativePath.trim();
    if (!targetRoot || !targetPath) return;
    const pathChanged = state.root !== targetRoot || state.historyPath !== targetPath;
    if (state.root !== targetRoot) resetGitPanelState(state, targetRoot);
    state.activated = true;
    state.historyPath = targetPath;
    state.history = [];
    state.historyRequested = 0;
    state.historyNextCursor = null;
    state.historyComplete = false;
    state.historyPaged = false;
    if (pathChanged) {
      resetGitCommitFilesView(gitCommitFiles, 'large');
      clearSelection('large');
    }
    if (!historySurfaceVisible) return;
    await loadHistory(null, false);
  }

  async function clearHistoryPath(): Promise<void> {
    if (!state.historyPath) return;
    state.historyPath = '';
    state.history = [];
    state.historyRequested = 0;
    state.historyNextCursor = null;
    state.historyComplete = false;
    state.historyPaged = false;
    resetGitCommitFilesView(gitCommitFiles, 'large');
    clearSelection('large');
    if (!historySurfaceVisible) return;
    await loadHistory(null, false);
  }

  async function selectFile(
    file: ProjectGitFileStatus,
    owner: GitSurfaceOwner = 'compact'
  ): Promise<void> {
    const root = state.root;
    if (!root) return;

    state.diffRevision += 1;
    const revision = state.diffRevision;
    state.selectedPaths[owner] = file.relativePath;
    state.diffOwner = owner;
    const commitSelection = gitCommitFilesView(gitCommitFiles, owner);
    commitSelection.selectedCommitSha = '';
    commitSelection.selectedRelativePath = '';
    state.selectedPath = file.relativePath;
    state.selectedDiff = null;
    state.diffError = '';
    publishGitDiagnostics();

    const id = diffGuard.next();
    state.diffLoading = true;
    diffRead.abort();
    diffRead = new AbortController();

    try {
      const diff = await withGitDiffTimeout(
        (signal) => backend.readDiff(root, absolutePathWithin(root, file.relativePath), signal),
        diffRead.signal,
        diffTimeoutMs
      );
      if (!stillCurrent(diffGuard, id, root) || state.diffRevision !== revision) return;
      if (!diff) {
        state.desktopOnly = true;
        state.diffError = DESKTOP_ONLY_MESSAGE;
        return;
      }
      state.selectedDiff = diff;
      publishGitDiagnostics();
    } catch (error) {
      if (!stillCurrent(diffGuard, id, root) || state.diffRevision !== revision) return;
      state.diffError = describeGitError(error, 'Could not read the changes for this file.', rail.remoteProfiles);
    } finally {
      if (stillCurrent(diffGuard, id, root) && state.diffRevision === revision) {
        state.diffLoading = false;
      }
    }
  }

  function clearSelection(owner: GitSurfaceOwner | null = null): void {
    if (owner === null || state.diffOwner === owner) {
      diffGuard.invalidate();
      diffRead.abort();
    }
    clearSelectedGitFile(state, owner);
    publishGitDiagnostics();
  }

  function activate(root: string | null): void {
    // Status is intentionally absent in history-only mode. Repository identity,
    // not the presence of one optional projection, makes activation idempotent.
    if (root === state.root && state.activated) return;
    statusGuard.invalidate();
    historyGuard.invalidate();
    diffGuard.invalidate();
    diffRead.abort();
    resetGitPanelState(state, root);
    publishGitDiagnostics();
    void publishSourceControl();
    if (!root) return;
    state.activated = true;
    // Status and history are visible-surface projections. Their owners request
    // them after activation so a history-only surface does not read status.
  }

  async function showStoredDiff(
    root: string,
    relativePath: string,
    owner: GitSurfaceOwner = 'compact'
  ): Promise<void> {
    const folder = root.trim();
    const path = relativePath.trim();
    if (!folder || !path) return;

    activate(folder);
    const id = diffGuard.next();
    diffRead.abort();
    // A remembered diff can outlive the file or the change it described. Get
    // current status before touching the path so a stale session snapshot is
    // cleared instead of surfacing the backend's missing-file error.
    if (!state.status) await refreshStatus();
    if (!stillCurrent(diffGuard, id, folder)) return;
    const known = (state.status?.files ?? []).find((file) => file.relativePath === path);
    if (known) return selectFile(known, owner);
    clearSelection(owner);
  }

  /** Re-read the diff on screen after an action changed the working tree. */
  function refreshSelectedDiff(): void {
    const selected = state.selectedPath;
    if (selected === '') return;
    const file = (state.status?.files ?? []).find((entry) => entry.relativePath === selected);
    if (!file) {
      clearSelection(state.diffOwner);
      return;
    }
    void selectFile(file, state.diffOwner ?? 'compact');
  }

  /**
   * The one place a working-tree action runs. Every action returns the fresh
   * status alongside its message, so the panel never needs a follow-up status
   * read — it adopts the result and supersedes any status read still in flight.
   */
  async function runAction(
    kind: GitActionKind,
    run: (root: string) => Promise<GitActionResult | null>,
    options: { reloadHistory: boolean }
  ): Promise<boolean> {
    const root = state.root;
    if (!root || state.actionBusy !== '') return false;

    state.actionBusy = kind;
    state.actionStatus = '';
    state.actionError = '';

    try {
      const result = await run(root);
      if (state.root !== root) return false;
      if (!result) {
        state.desktopOnly = true;
        state.actionStatus = DESKTOP_ONLY_MESSAGE;
        return false;
      }
      // The fresh status arrived with the result: take it, and make sure a
      // slower status read started earlier cannot overwrite it.
      statusGuard.invalidate();
      state.statusLoading = false;
      state.statusError = '';
      state.desktopOnly = false;
      state.status = result.status;
      state.actionStatus = result.message;
      void publishSourceControl();
      refreshSelectedDiff();
      if (options.reloadHistory) void refreshHistory();
      return true;
    } catch (error) {
      if (state.root !== root) return false;
      state.actionError = describeGitError(error, 'The git command did not finish.', rail.remoteProfiles);
      return false;
    } finally {
      if (state.actionBusy === kind) state.actionBusy = '';
    }
  }

  function cleanPaths(paths: string[]): string[] {
    return paths.map((path) => path.trim()).filter((path) => path !== '');
  }

  return {
    state,

    activate,

    refresh,
    refreshStatus,
    refreshHistory,
    loadMoreHistory,
    ensureHistorySurface,
    releaseHistorySurface,
    showFileHistory,
    clearHistoryPath,
    selectFile,
    showStoredDiff,
    clearSelection,

    async readWorkingDiff(relativePath: string, signal: AbortSignal): Promise<SourceGitDiff | null> {
      const root = state.root;
      if (!root) return null;
      return withGitDiffTimeout(
        (read) => backend.readDiff(root, absolutePathWithin(root, relativePath), read),
        signal,
        diffTimeoutMs
      );
    },

    async stagePaths(paths: string[]): Promise<void> {
      const wanted = cleanPaths(paths);
      if (wanted.length === 0) return;
      await runAction('stage', (root) => backend.stage(root, wanted), { reloadHistory: false });
    },

    async unstagePaths(paths: string[]): Promise<void> {
      const wanted = cleanPaths(paths);
      if (wanted.length === 0) return;
      await runAction('unstage', (root) => backend.unstage(root, wanted), { reloadHistory: false });
    },

    async commit(message?: string): Promise<void> {
      const text = (message ?? state.commitMessage).trim();
      if (text === '') {
        state.actionError = 'Type a commit message first.';
        return;
      }
      const before = state.status;
      await runAction('commit', (root) => backend.commit(root, text), { reloadHistory: true });
      // Only clear the box on a commit that actually happened.
      if (state.actionError === '' && state.status !== before) state.commitMessage = '';
    },
    setAmend: amend.set,
    async amendCommit(message?: string): Promise<void> {
      const text = amend.messageFor((message ?? state.commitMessage).trim());
      const amended = await runAction('amend', (root) => backend.amend(root, text), { reloadHistory: true });
      if (amended) amend.finish();
    },

    async discardPaths(paths: string[]): Promise<void> {
      const wanted = cleanPaths(paths);
      if (wanted.length === 0) return;
      await runAction('discard', (root) => backend.discard(root, wanted), {
        reloadHistory: false
      });
    },

    async discardAll(includeUntracked: boolean): Promise<void> {
      await runAction('discard', (root) => backend.discardAll(root, includeUntracked), {
        reloadHistory: false
      });
    },

    async listBranches(signal?: AbortSignal): Promise<GitBranchList | null> {
      const root = state.root;
      if (!root) return null;
      const list = await backend.listBranches(root, signal);
      if (state.root !== root) return null;
      if (list && parseRemoteWorkspacePath(root) && list.remotes === undefined) {
        throw new Error('The remote backend is older than this Assembly app. Update the backend in Settings > Connections, then retry Publish.');
      }
      return list;
    },

    async createBranch(name: string, checkout: boolean): Promise<void> {
      const wanted = name.trim();
      if (wanted === '') {
        state.actionError = 'Type a branch name first.';
        return;
      }
      await runAction('branch', (root) => backend.createBranch(root, wanted, checkout), {
        reloadHistory: checkout
      });
    },

    async switchBranch(name: string): Promise<void> {
      const wanted = name.trim();
      if (wanted === '') return;
      await runAction('branch', (root) => backend.switchBranch(root, wanted), {
        reloadHistory: true
      });
    },

    async stashChanges(includeUntracked: boolean, message: string): Promise<void> {
      await runAction('stash', (root) => backend.stash(root, includeUntracked, message.trim()), {
        reloadHistory: false
      });
    },

    async popStash(index: number | null): Promise<void> {
      await runAction('stash', (root) => backend.popStash(root, index), { reloadHistory: false });
    },

    async listStashes(): Promise<GitStashEntry[] | null> {
      const root = state.root;
      if (!root) return null;
      return backend.listStashes(root);
    },

    async runRemoteAction(action: 'fetch' | 'pull' | 'push'): Promise<void> {
      const run =
        action === 'fetch'
          ? (root: string) => backend.fetch(root)
          : action === 'pull'
            ? (root: string) => backend.pull(root)
            : (root: string) => backend.push(root);
      await runAction(action, run, { reloadHistory: action !== 'fetch' });
    },

    async addRemote(expectedRoot: string, name: string, url: string): Promise<boolean> {
      if (state.root !== expectedRoot) return false;
      return await runAction('publish', (root) => backend.addRemote(root, name, url), { reloadHistory: true });
    },

    async publishBranch(expectedRoot: string, expectedBranch: string, remote: string): Promise<void> {
      if (state.root !== expectedRoot) return;
      if (state.status?.branch !== expectedBranch || state.status?.hasUpstream) {
        state.actionError = 'The branch changed. Refresh Source Control and try again.';
        return;
      }
      if (!remote) {
        state.actionError = 'This repository has no configured remote. Add one before publishing.';
        return;
      }
      await runAction('publish', (root) => backend.publish(root, expectedBranch, remote), { reloadHistory: true });
    },

    async initRepository(signal: AbortSignal): Promise<void> {
      const root = state.root;
      if (!root || state.actionBusy !== '') return;
      state.actionBusy = 'init';
      state.actionStatus = '';
      state.actionError = '';
      try {
        await backend.initRepository(root, signal);
        if (signal.aborted || state.root !== root) return;
        // Status and history both still hold "not a git repository".
        await refresh();
      } catch (error) {
        if (signal.aborted || state.root !== root) return;
        state.actionError = describeGitError(error, 'Git could not set up the folder.', rail.remoteProfiles);
      } finally {
        if (state.actionBusy === 'init') state.actionBusy = '';
      }
    }
  };
}

/**
 * The service the panel uses. A lane-scoped singleton bound to `gitPanel`,
 * because the panel component takes no props (the shell's panel contract).
 * Constructing it performs no IO.
 *
 * WHERE ITS ANSWERS COME FROM. In the desktop app, the desktop's own git
 * commands. In a browser those answer nothing at all, so it reads through the
 * dev server's git bridge instead — which can read a repository but can never
 * change one, deliberately. Without this the panel in a browser tab could only
 * ever say "desktop app only", and the panes could not be looked at outside the
 * app at all.
 */
export const gitService = createGitService();
