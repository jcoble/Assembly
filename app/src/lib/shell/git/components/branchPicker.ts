/**
 * branchPicker.ts — the branch menu's view model.
 *
 * Everything a person can get wrong about branches is decided here rather than
 * in the component: which branch is the current one, what the filter box
 * matches, and whether a typed name is one git will actually accept. The Rust
 * side validates the name again before it runs anything — this copy exists so
 * the reason appears under the box while typing instead of as a failed command.
 *
 * Pure module: no state, no IO, no `$effect`.
 */

import type { GitBranchSummary } from '$lib/tauriSource';

/** The characters git refuses in a branch name, plus the ones a bare argument cannot carry. */
const REFUSED_CHARACTERS = ['~', '^', ':', '?', '*', '[', '\\'];

/** Branch names longer than this are the backend's limit, mirrored here. */
export const BRANCH_NAME_LIMIT = 200;

/** Match on the whole name, case-insensitively. A branch list is not a search engine. */
export function filterBranches(
  branches: GitBranchSummary[],
  query: string
): GitBranchSummary[] {
  const wanted = query.trim().toLowerCase();
  if (wanted === '') return branches;
  return branches.filter((branch) => branch.name.toLowerCase().includes(wanted));
}

/** The dim second line on a branch row: where it tracks, and its last subject. */
export function describeBranchRow(branch: GitBranchSummary): string {
  const parts = [branch.upstream ? `tracks ${branch.upstream}` : 'no remote branch'];
  if (branch.subject) parts.push(branch.subject);
  return parts.join(' · ');
}

/**
 * The worktree to move the session to when this branch is picked, or null to
 * check it out here. Git refuses to check out a branch another worktree has, so
 * the session goes to that worktree instead. For a remote repository the
 * workspace bridge has already given the path its machine prefix.
 */
export function branchWorktreeRoot(branch: GitBranchSummary): string | null {
  return branch.isCurrent || !branch.worktreePath ? null : branch.worktreePath;
}

/**
 * Why a typed branch name cannot be used, or '' when it can. The empty case is
 * deliberately quiet: an empty box is not a mistake, it is a box nobody has
 * typed in yet, so the button is simply off and says nothing red.
 */
export function branchNameProblem(name: string, existing: string[] = []): string {
  const trimmed = name.trim();
  if (trimmed === '') return '';
  if (trimmed.startsWith('-')) return 'A branch name cannot start with a dash.';
  if (trimmed.length > BRANCH_NAME_LIMIT) return 'That branch name is too long.';
  if (/\s/.test(trimmed)) return 'Branch names cannot contain spaces.';
  const refused = REFUSED_CHARACTERS.find((character) => trimmed.includes(character));
  if (refused) return `Branch names cannot contain ${refused}`;
  if (trimmed.includes('..')) return 'Branch names cannot contain two dots in a row.';
  if (trimmed.startsWith('/') || trimmed.endsWith('/')) return 'Branch names cannot start or end with a slash.';
  if (trimmed.endsWith('.lock')) return 'Branch names cannot end with .lock';
  if (existing.some((branch) => branch === trimmed)) return `${trimmed} already exists.`;
  return '';
}

/** Can the "Create branch" button be pressed with what has been typed? */
export function canCreateBranch(
  name: string,
  existing: string[],
  options: { canWrite: boolean; busy: boolean }
): boolean {
  if (!options.canWrite || options.busy) return false;
  return name.trim() !== '' && branchNameProblem(name, existing) === '';
}

/** The branch line at the top of the panel, said plainly when there is none. */
export function describeCurrentBranch(current: string | null | undefined): string {
  const name = (current ?? '').trim();
  return name === '' ? 'no branch checked out' : name;
}
