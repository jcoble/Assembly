/**
 * gitCommitFilesStore.svelte.ts — which commits are open in the graph, and what
 * each one changed.
 *
 * The existing `gitPanelStore` covers the working copy: what has changed, what
 * is staged, which file's diff is on screen. It knows nothing about the history
 * beyond a list of commit lines, because until this round nothing could ask
 * what a commit contained. This store holds that second half, and it is a
 * separate file rather than a few more fields in the first one so the panel
 * that already works keeps its own tested shape.
 *
 * Same two rules as its neighbour:
 *
 *  1. **No backend, ever.** Every git call lives in
 *     `gitCommitFilesService.ts` and lands here as a plain assignment.
 *  2. **No `$effect`.** Nothing here reacts; it is a value bag with a few pure
 *     functions over it, which is what lets the Node test read every sentence
 *     this panel can say.
 */

import type { GitCommitFileChange } from './gitBackendExtra.ts';
import type { GitSurfaceOwner } from './gitPanelStore.svelte.ts';

/** Said when nothing on this page can answer a question about the history. */
export const COMMIT_FILES_DESKTOP_ONLY_MESSAGE =
  'Reading a commit runs in the desktop app. Nothing here can answer that.';

/**
 * A merge does not carry changes of its own — it records that two lines of work
 * came together. Git therefore lists no files for it, and the row has to say so
 * rather than sit on a spinner waiting for a list that is never coming.
 */
export const MERGE_HAS_NO_CHANGES_MESSAGE =
  'This is a merge, and a merge brings no changes of its own. Open the commits it brought in to see the work.';

/**
 * Git prints a file name containing anything but plain ASCII as octal escapes
 * (`"\303\251.txt"`) unless it is told otherwise, and the desktop app does not
 * tell it. Handing that name back to git matches no file, so the answer is a
 * blank diff that reads exactly like "this file did not change". Saying so is
 * the only honest thing to do until the desktop side is fixed.
 */
export const UNREADABLE_PATH_MESSAGE =
  'This file has a name git could not print in plain letters, so its changes cannot be shown here yet.';

/** What we know about one commit's file list. */
export interface GitCommitFilesEntry {
  files: GitCommitFileChange[];
  loading: boolean;
  /** We have a complete answer — even if that answer is "no files". */
  loaded: boolean;
  error: string;
}

export interface GitCommitFilesViewState {
  /** Commit id -> is its row open in this presentation. */
  expanded: Record<string, boolean>;
  selectedCommitSha: string;
  selectedRelativePath: string;
}

export interface GitCommitFilesState {
  /** Increments whenever loaded commit detail is released. */
  revision: number;
  /** The folder the panel was pointed at, which may be inside the repository. */
  root: string | null;
  /** The top of that repository, once it has been worked out. */
  repositoryTop: string | null;
  /** Commit id -> what it changed. */
  byCommit: Record<string, GitCommitFilesEntry>;
  /** Lightweight interaction state; both presentations share `byCommit`. */
  views: Record<GitSurfaceOwner, GitCommitFilesViewState>;
}

function createGitCommitFilesViewState(): GitCommitFilesViewState {
  return { expanded: {}, selectedCommitSha: '', selectedRelativePath: '' };
}

export function createGitCommitFilesEntry(): GitCommitFilesEntry {
  return { files: [], loading: false, loaded: false, error: '' };
}

export function createGitCommitFilesState(): GitCommitFilesState {
  return {
    revision: 0,
    root: null,
    repositoryTop: null,
    byCommit: {},
    views: {
      compact: createGitCommitFilesViewState(),
      large: createGitCommitFilesViewState()
    }
  };
}

/** Forget everything about the old repository and point at `root`. */
export function resetGitCommitFilesState(
  state: GitCommitFilesState,
  root: string | null
): void {
  state.revision = (state.revision ?? 0) + 1;
  state.root = root;
  state.repositoryTop = null;
  state.byCommit = {};
  state.views = {
    compact: createGitCommitFilesViewState(),
    large: createGitCommitFilesViewState()
  };
}

export function gitCommitFilesView(
  state: GitCommitFilesState,
  owner: GitSurfaceOwner = 'compact'
): GitCommitFilesViewState {
  return state.views[owner];
}

export function isCommitExpanded(
  state: GitCommitFilesState,
  sha: string,
  owner: GitSurfaceOwner = 'compact'
): boolean {
  return gitCommitFilesView(state, owner).expanded[sha] === true;
}

/** Release one presentation without discarding payload another still displays. */
export function resetGitCommitFilesView(
  state: GitCommitFilesState,
  owner: GitSurfaceOwner
): void {
  const opened = Object.keys(state.views[owner].expanded);
  state.views[owner] = createGitCommitFilesViewState();
  const other: GitSurfaceOwner = owner === 'compact' ? 'large' : 'compact';
  for (const sha of opened) {
    if (!state.views[other].expanded[sha]) delete state.byCommit[sha];
  }
}

/** What we know about this commit, or a blank entry when we know nothing. */
export function commitFilesEntry(
  state: GitCommitFilesState,
  sha: string
): GitCommitFilesEntry {
  return state.byCommit[sha] ?? createGitCommitFilesEntry();
}

/** "3 files", for the line next to an open commit. */
export function summarizeCommitFiles(files: readonly GitCommitFileChange[]): string {
  if (files.length === 0) return 'No files';
  return files.length === 1 ? '1 file' : `${files.length} files`;
}

/**
 * The sentence an open commit shows instead of a file list, or '' when there is
 * a list to show. Every case ends in words: nothing here can stay a spinner.
 */
export function describeCommitFiles(entry: GitCommitFilesEntry, isMerge: boolean): string {
  if (entry.error !== '') return entry.error;
  if (entry.loading) return 'Reading what this commit changed…';
  if (entry.files.length > 0) return '';
  if (!entry.loaded) return 'Nothing read yet.';
  return isMerge ? MERGE_HAS_NO_CHANGES_MESSAGE : 'This commit changed no files.';
}

/**
 * Is this a file name git could not print? Two shapes give it away: the octal
 * escapes git writes for anything outside plain ASCII (`\303\251`), and the
 * double quotes it wraps such a name in.
 */
export function isUnreadableGitPath(relativePath: string): boolean {
  const value = relativePath ?? '';
  if (value.startsWith('"') || value.endsWith('"')) return true;
  return /\\[0-3][0-7]{2}/.test(value);
}

/**
 * A path split for the row: the file's name, and the folder it sits in.
 *
 * Git reports a folder full of untracked files as the folder itself, with a
 * trailing slash (`.vscode/`). Splitting that naively leaves the name empty and
 * the row shows nothing but a dimmed path, so the trailing slash stays with the
 * name — which is also how it reads: `.vscode/` is a folder.
 */
export function splitRepositoryPath(relativePath: string): { name: string; folder: string } {
  const value = relativePath ?? '';
  const isFolder = value.endsWith('/');
  const trimmed = isFolder ? value.slice(0, -1) : value;
  const index = trimmed.lastIndexOf('/');
  const name = index < 0 ? trimmed : trimmed.slice(index + 1);
  return {
    name: isFolder ? `${name}/` : name,
    folder: index < 0 ? '' : trimmed.slice(0, index)
  };
}

/** The reactive state the commit graph reads and the service writes. */
export const gitCommitFiles = $state<GitCommitFilesState>(createGitCommitFilesState());
