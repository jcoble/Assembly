/**
 * sourceControlSections.ts — how the panel splits the working copy into the
 * three lists it draws, and the counts beside its title.
 *
 * The backend already decides what each file's state is (`badge`, `indexStatus`,
 * `worktreeStatus`), so nothing here reads git output. It sorts and groups, and
 * that is all — which is why both functions are pure over the status value and
 * can be checked without a browser.
 *
 * Staged, Changes and Untracked, in that order — the order a commit is put
 * together in. A file that was staged and then edited again appears in both
 * Staged and Changes, because that is what git reports about it and picking
 * one of the two would hide half of what is there.
 */

import type { ProjectGitFileStatus, ProjectGitStatus } from '../../../../tauriSource.ts';
import {
  hasGitFileUnstagedChanges,
  isGitFileStaged,
  isGitFileUntracked
} from '../../gitPanelStore.svelte.ts';

export interface SourceControlSection {
  id: 'staged' | 'changed' | 'untracked';
  label: string;
  files: ProjectGitFileStatus[];
}

export interface SourceControlDiffstat {
  /** How many files the working copy has changed in any way. */
  filesChanged: number;
  /** How many of those are new files git is not tracking yet. */
  untracked: number;
}

function byPath(left: ProjectGitFileStatus, right: ProjectGitFileStatus): number {
  return left.relativePath.localeCompare(right.relativePath);
}

/** The three sections, each sorted by relativePath. */
export function sourceControlSections(status: ProjectGitStatus | null): SourceControlSection[] {
  const files = status?.files ?? [];
  return [
    {
      id: 'staged',
      label: 'Staged',
      files: files.filter(isGitFileStaged).sort(byPath)
    },
    {
      id: 'changed',
      label: 'Changes',
      files: files
        .filter((file) => !isGitFileUntracked(file) && hasGitFileUnstagedChanges(file))
        .sort(byPath)
    },
    {
      id: 'untracked',
      label: 'Untracked',
      files: files.filter(isGitFileUntracked).sort(byPath)
    }
  ];
}

/**
 * How many untracked files Discard All will delete. Past the list's cap the
 * hidden files are counted as untracked, so this can overstate the number but
 * never understates it.
 */
export function discardAllUntrackedCount(status: ProjectGitStatus | null): number {
  const files = status?.files ?? [];
  const tracked = files.filter((file) => !isGitFileUntracked(file)).length;
  return (status?.totalFiles ?? files.length) - tracked;
}

/**
 * The counts beside the panel title. Line totals are not shown here; the status
 * command reports them only as whole-tree sums (`additions`/`deletions`), which
 * the Changes tab in the top row displays.
 */
export function sourceControlDiffstat(status: ProjectGitStatus | null): SourceControlDiffstat {
  const files = status?.files ?? [];
  return {
    filesChanged: status?.totalFiles ?? files.length,
    untracked: files.filter(isGitFileUntracked).length
  };
}
