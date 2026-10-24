/**
 * gitCommitFilesService.ts — the git calls behind an open commit in the graph.
 *
 * The same rules `gitService.ts` is built on, for the same reasons:
 *
 *  - **Nothing loads at import, and nothing reacts.** No `$effect`. A commit's
 *    file list is read when somebody opens that commit, and never otherwise.
 *  - **Every question goes to the top of the repository.** `git show <sha> --
 *    <path>` matches the path against the folder git is run in, so asking from
 *    a subfolder matches nothing and reports success — a blank answer that
 *    reads as "this file did not change". The panel is often pointed at a
 *    session's working folder, which is frequently NOT the top, so the top is
 *    worked out once per repository and every call uses it.
 *  - **A slow answer never overwrites a newer one.** Same monotonic request id
 *    as the rest of the panel.
 *  - **No stand-in data.** When neither the desktop app nor the dev server can
 *    answer, the row says so in one sentence.
 *
 * The diff itself is shown by the existing `GitDiffView`, which reads
 * `gitPanel`. So picking a file inside a commit writes into that same state —
 * after letting go of whatever working-copy diff was on screen, so the two
 * cannot race each other into the view.
 */

import {
  readCommitFileDiff,
  readCommitFiles,
  resolveRepositoryTop,
  withGitDiffTimeout,
  type GitCommitFileChange
} from './gitBackendExtra.ts';
import {
  COMMIT_FILES_DESKTOP_ONLY_MESSAGE,
  UNREADABLE_PATH_MESSAGE,
  commitFilesEntry,
  gitCommitFiles,
  gitCommitFilesView,
  isCommitExpanded,
  isUnreadableGitPath,
  resetGitCommitFilesState,
  type GitCommitFilesState
} from './gitCommitFilesStore.svelte.ts';
import { describeGitError, gitPanel, type GitSurfaceOwner } from './gitPanelStore.svelte.ts';
import { gitService } from './gitService.ts';
import type { SourceGitDiff } from '../../tauriSource.ts';
import { rail } from '../stores/sessionRailStore.svelte.ts';

/** The part of the panel state a commit's diff is written into. */
export interface CommitDiffTarget {
  root: string | null;
  selectedPath: string;
  selectedPaths: Record<GitSurfaceOwner, string>;
  diffOwner: GitSurfaceOwner | null;
  diffRevision: number;
  selectedDiff: SourceGitDiff | null;
  diffLoading: boolean;
  diffError: string;
}

export interface GitCommitFilesServiceOptions {
  state?: GitCommitFilesState;
  panel?: CommitDiffTarget;
  owner?: GitSurfaceOwner;
  /** The top of the repository a folder belongs to, or null when unknown. */
  resolveTop?(root: string): Promise<string | null>;
  readFiles?(root: string, sha: string): Promise<GitCommitFileChange[] | null>;
  readDiff?(
    root: string,
    sha: string,
    relativePath: string,
    signal: AbortSignal
  ): Promise<SourceGitDiff | null>;
  /** Let go of the working-copy diff on screen before showing a commit's one. */
  clearPanelSelection?(): void;
  diffTimeoutMs?: number;
}

export interface GitCommitFilesService {
  readonly state: GitCommitFilesState;
  /** Point at a repository. Calling it again with the same folder does nothing. */
  activate(root: string | null): void;
  /** Release expanded commit rows and their loaded file payloads. */
  release(): void;
  /** Open or close one commit, reading its file list the first time it opens. */
  toggleCommit(sha: string, isMerge: boolean): Promise<void>;
  /** Read one open commit's file list. */
  loadCommitFiles(sha: string, isMerge: boolean): Promise<void>;
  /** Show what one file of one commit changed. */
  selectCommitFile(sha: string, file: GitCommitFileChange): Promise<void>;
  /** Stop showing a commit's file. */
  clearSelection(): void;
  /** Re-materialize this surface's retained selection into the shared Diff. */
  restoreSelection(): Promise<void>;
}

export function createGitCommitFilesService(
  options: GitCommitFilesServiceOptions = {}
): GitCommitFilesService {
  const state = options.state ?? gitCommitFiles;
  const panel = options.panel ?? gitPanel;
  const owner = options.owner ?? 'compact';
  const view = () => gitCommitFilesView(state, owner);
  const resolveTop = options.resolveTop ?? resolveRepositoryTop;
  const readFiles = options.readFiles ?? readCommitFiles;
  const readDiff = options.readDiff ?? readCommitFileDiff;
  const clearPanelSelection = options.clearPanelSelection ?? (() => gitService.clearSelection(owner));
  const diffTimeoutMs = options.diffTimeoutMs ?? 20_000;

  /** The one in-flight look-up of the repository top, shared by every caller. */
  let topRequest: Promise<string> | null = null;
  /** Stops the selected file's diff read when another is picked or the view lets go. */
  let diffRead = new AbortController();

  function currentRoot(): string | null {
    return state.root ?? panel.root;
  }

  async function repositoryTopFor(root: string): Promise<string> {
    if (state.repositoryTop) return state.repositoryTop;
    if (!topRequest) {
      topRequest = resolveRepositoryTopOnce(root);
    }
    return topRequest;
  }

  async function resolveRepositoryTopOnce(root: string): Promise<string> {
    let resolved: string | null = null;
    try {
      resolved = await resolveTop(root);
    } catch {
      // Not knowing the top is not a failure worth showing: fall back to
      // the folder we were given, which is the top in the usual case.
      resolved = null;
    }
    const top = (resolved ?? '').trim() === '' ? root : (resolved as string).trim();
    if (state.root === root) state.repositoryTop = top;
    return top;
  }

  async function loadCommitFiles(sha: string): Promise<void> {
    const root = currentRoot();
    if (!root || sha.trim() === '') return;

    const revision = state.revision;
    state.byCommit[sha] = { ...commitFilesEntry(state, sha), loading: true, error: '' };
    const entry = state.byCommit[sha];

    const stillCurrent = () =>
      state.byCommit[sha] === entry &&
      state.root === root &&
      state.revision === revision &&
      (isCommitExpanded(state, sha, 'compact') || isCommitExpanded(state, sha, 'large'));

    try {
      const top = await repositoryTopFor(root);
      const files = await readFiles(top, sha);
      if (!stillCurrent()) return;
      if (!files) {
        Object.assign(entry, {
          files: [],
          loaded: false,
          error: COMMIT_FILES_DESKTOP_ONLY_MESSAGE
        });
        return;
      }
      Object.assign(entry, { files, loaded: true, error: '' });
    } catch (error) {
      if (!stillCurrent()) return;
      Object.assign(entry, {
        files: [],
        loaded: false,
        error: describeGitError(error, 'Could not read what this commit changed.', rail.remoteProfiles)
      });
    } finally {
      if (stillCurrent()) entry.loading = false;
    }
  }

  async function selectCommitFile(sha: string, file: GitCommitFileChange): Promise<void> {
    const root = currentRoot();
    if (!root) return;
    const revision = state.revision;

    // Whatever the working-copy side was showing is let go of first, so its own
    // in-flight read cannot land on top of this one.
    clearPanelSelection();
    view().selectedCommitSha = sha;
    view().selectedRelativePath = file.relativePath;
    panel.selectedPath = file.relativePath;
    panel.selectedPaths[owner] = file.relativePath;
    panel.diffOwner = owner;
    panel.diffRevision += 1;
    panel.selectedDiff = null;
    panel.diffError = '';

    if (isUnreadableGitPath(file.relativePath)) {
      diffRead.abort();
      panel.diffLoading = false;
      panel.diffError = UNREADABLE_PATH_MESSAGE;
      return;
    }

    diffRead.abort();
    diffRead = new AbortController();
    const { signal } = diffRead;
    panel.diffLoading = true;
    const panelRevision = panel.diffRevision;
    const stillCurrent = () =>
      !signal.aborted &&
      state.root === root &&
      state.revision === revision &&
      panel.diffRevision === panelRevision;

    try {
      const diff = await withGitDiffTimeout(
        async (read) => {
          const top = await repositoryTopFor(root);
          read.throwIfAborted();
          return readDiff(top, sha, file.relativePath, read);
        },
        signal,
        diffTimeoutMs
      );
      if (!stillCurrent()) return;
      if (!diff) {
        panel.diffError = COMMIT_FILES_DESKTOP_ONLY_MESSAGE;
        return;
      }
      panel.selectedDiff = diff;
    } catch (error) {
      if (!stillCurrent()) return;
      panel.diffError = describeGitError(error, 'Could not read the changes for this file.', rail.remoteProfiles);
    } finally {
      if (stillCurrent()) panel.diffLoading = false;
    }
  }

  function clearSelection(): void {
    diffRead.abort();
    view().selectedCommitSha = '';
    view().selectedRelativePath = '';
    clearPanelSelection();
  }

  async function restoreSelection(): Promise<void> {
    const selected = view();
    if (!selected.selectedCommitSha || !selected.selectedRelativePath) return;
    await selectCommitFile(selected.selectedCommitSha, {
      relativePath: selected.selectedRelativePath,
      status: 'modified',
      badge: 'M'
    });
  }

  return {
    state,

    activate(root: string | null): void {
      if (root === state.root) return;
      topRequest = null;
      diffRead.abort();
      if (view().selectedCommitSha !== '' || panel.diffLoading) clearPanelSelection();
      resetGitCommitFilesState(state, root);
    },

    release(): void {
      topRequest = null;
      diffRead.abort();
      if (view().selectedCommitSha !== '' || panel.diffLoading) clearPanelSelection();
      resetGitCommitFilesState(state, null);
    },

    async toggleCommit(sha: string, isMerge: boolean): Promise<void> {
      if (isCommitExpanded(state, sha, owner)) {
        delete view().expanded[sha];
        const other: GitSurfaceOwner = owner === 'compact' ? 'large' : 'compact';
        if (!isCommitExpanded(state, sha, other)) {
          delete state.byCommit[sha];
        }
        if (view().selectedCommitSha === sha) clearSelection();
        return;
      }
      view().expanded[sha] = true;
      const entry = commitFilesEntry(state, sha);
      if (entry.loaded || entry.loading) return;
      await loadCommitFiles(sha);
      void isMerge; // the merge wording is chosen when the row is drawn
    },

    async loadCommitFiles(sha: string, isMerge: boolean): Promise<void> {
      void isMerge;
      await loadCommitFiles(sha);
    },

    selectCommitFile,

    clearSelection,
    restoreSelection
  };
}

/**
 * The service the commit graph uses. A lane-scoped singleton bound to
 * `gitCommitFiles` and `gitPanel`, because the panel components take no
 * required props. Constructing it performs no IO.
 */
export const gitCommitFilesService = createGitCommitFilesService();
