/**
 * gitHistoryFilters.ts — every decision the center-lane Git History view makes
 * before it draws a single row.
 *
 * PURE: no state, no backend, no DOM, no dates. Commits in, rows out, the same
 * way every time — which is why `scripts/gitGraphViewModel.test.mjs` can check
 * all of it in plain Node.
 *
 * WHAT A BRANCH FILTER CAN HONESTLY MEAN HERE. The app reads the history as one
 * flat list of commits, newest first, and branch names only decorate the commit
 * each one currently points at. Nothing in that list says which branch a commit
 * "belongs to" — the question does not have an answer in git either. What does
 * have an answer is reachability: the commit a branch points at, and everything
 * that commit descends from. That is what `gitHistoryBranchScope` works out, and
 * it does it over THE COMMITS THAT ARE LOADED, because those are the only
 * parents we have. Choosing a branch and then loading more history therefore
 * widens the answer, which is correct: more of the line has been read.
 *
 * THE UNCOMMITTED ROW. Work that has not been committed has no author, no hash
 * and no message, so it cannot match an author or a search — a filter on either
 * hides it rather than pretending it matched. A branch filter keeps it, because
 * uncommitted work is on whichever branch is checked out.
 */
import type { ProjectGitStatus } from '../../tauriSource.ts';
import type { GitGraphCommitRow } from './gitGraphViewModel.ts';
import { buildGitStatusFileGroups } from './gitPanelStore.svelte.ts';

export interface GitHistoryFilter {
  /** A branch or remote-branch name, or '' for every line of history. */
  branch: string;
  /** An exact author name, or '' for everyone. */
  author: string;
  /** Free text matched against the commit's message, hash, author and refs. */
  search: string;
}

export const EMPTY_GIT_HISTORY_FILTER: GitHistoryFilter = {
  branch: '',
  author: '',
  search: ''
};

/** One entry of the repository picker. */
export interface GitHistoryRepositoryOption {
  /** Absolute path of the checkout or worktree. */
  path: string;
  /** The last segment of that path — what the picker shows. */
  label: string;
  /** The branch checked out there, or '' when it is not known. */
  branch: string;
}

/** The row above the newest commit, when the working copy has changes in it. */
export interface GitHistoryUncommittedRow {
  changedCount: number;
  /** "3 files". */
  countLabel: string;
  /** "Staged 1 · Changed 1 · New files 1". */
  detail: string;
}

/** True when any of the three filters is doing something. */
export function isGitHistoryFilterActive(filter: GitHistoryFilter): boolean {
  return (
    filter.branch.trim() !== '' || filter.author.trim() !== '' || filter.search.trim() !== ''
  );
}

/** Every author in the loaded commits, once each, in a settled order. */
export function gitHistoryAuthors(rows: readonly GitGraphCommitRow[]): string[] {
  const authors = new Set<string>();
  for (const row of rows) {
    const author = row.author.trim();
    if (author !== '') authors.add(author);
  }
  return [...authors].sort((left, right) => left.localeCompare(right));
}

/**
 * Every branch name the loaded commits carry, local and remote, once each.
 * Tags are left out on purpose: a tag names one commit, so filtering a line of
 * history by it would only ever show that commit and its ancestors under a
 * label that does not read that way.
 */
export function gitHistoryBranches(rows: readonly GitGraphCommitRow[]): string[] {
  const branches = new Set<string>();
  for (const row of rows) {
    for (const label of row.refs.branchLabels) branches.add(label);
    for (const label of row.refs.remoteLabels) branches.add(label);
  }
  return [...branches].sort((left, right) => left.localeCompare(right));
}

/**
 * The commits a branch reaches inside the loaded list: its tip, and everything
 * that tip descends from. `null` means no branch was chosen — which is not the
 * same as an empty scope, and the difference is the whole filter.
 */
export function gitHistoryBranchScope(
  rows: readonly GitGraphCommitRow[],
  branch: string
): Set<string> | null {
  const wanted = branch.trim();
  if (wanted === '') return null;

  const bySha = new Map(rows.map((row) => [row.sha, row]));
  const queue = rows
    .filter(
      (row) => row.refs.branchLabels.includes(wanted) || row.refs.remoteLabels.includes(wanted)
    )
    .map((row) => row.sha);

  const reached = new Set<string>(queue);
  while (queue.length > 0) {
    const sha = queue.pop()!;
    for (const parent of bySha.get(sha)?.parentHint.parentShas ?? []) {
      if (reached.has(parent) || !bySha.has(parent)) continue;
      reached.add(parent);
      queue.push(parent);
    }
  }
  return reached;
}

/** The commits left after all three filters, in the order they came in. */
export function filterGitHistoryRows(
  rows: readonly GitGraphCommitRow[],
  filter: GitHistoryFilter
): GitGraphCommitRow[] {
  const scope = gitHistoryBranchScope(rows, filter.branch);
  const author = filter.author.trim();
  const search = filter.search.trim().toLowerCase();

  return rows.filter((row) => {
    if (scope && !scope.has(row.sha)) return false;
    if (author !== '' && row.author.trim() !== author) return false;
    if (search !== '' && !row.searchText.toLowerCase().includes(search)) return false;
    return true;
  });
}

/**
 * The "Uncommitted Changes" row, or `null` when there is nothing to show. A
 * status that has not been read yet is `null` too: an invented clean row would
 * say something we do not know.
 */
export function gitHistoryUncommittedRow(
  status: ProjectGitStatus | null,
  filter: GitHistoryFilter
): GitHistoryUncommittedRow | null {
  if (!status) return null;
  if (filter.author.trim() !== '' || filter.search.trim() !== '') return null;

  const files = status.files ?? [];
  if (files.length === 0) return null;

  const groups = buildGitStatusFileGroups(files);
  return {
    changedCount: files.length,
    countLabel: `${files.length} ${files.length === 1 ? 'file' : 'files'}`,
    detail: groups.map((group) => `${group.label} ${group.files.length}`).join(' · ')
  };
}

/** What the picker offers: the folder in front first, then every worktree the
 * app has read, each path once. */
export function gitHistoryRepositoryOptions(
  currentRoot: string | null,
  worktrees: readonly { path: string; branch?: string }[]
): GitHistoryRepositoryOption[] {
  const options: GitHistoryRepositoryOption[] = [];
  const seen = new Set<string>();

  const add = (path: string, branch: string): void => {
    const trimmed = path.replace(/\/+$/, '').trim();
    if (trimmed === '' || seen.has(trimmed)) return;
    seen.add(trimmed);
    options.push({ path: trimmed, label: lastSegment(trimmed), branch: branch.trim() });
  };

  // The folder in front leads the list, but its branch name is only known if a
  // worktree read has already named it — so look that up rather than showing it
  // blank next to entries that have one.
  const branchByPath = new Map<string, string>();
  for (const worktree of worktrees) {
    const path = (worktree.path ?? '').replace(/\/+$/, '').trim();
    if (path !== '' && !branchByPath.has(path)) branchByPath.set(path, worktree.branch ?? '');
  }

  if (currentRoot) {
    const trimmed = currentRoot.replace(/\/+$/, '').trim();
    add(trimmed, branchByPath.get(trimmed) ?? '');
  }
  for (const worktree of worktrees) add(worktree.path ?? '', worktree.branch ?? '');
  return options;
}

function lastSegment(path: string): string {
  const index = path.lastIndexOf('/');
  return index >= 0 ? path.slice(index + 1) : path;
}
