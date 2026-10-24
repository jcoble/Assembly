import {
  buildGitTaskSearchTarget,
  extractGitTaskIDs,
  normalizeGitTaskID,
  type GitTaskSearchTarget
} from './gitTaskLinks.ts';
import {
  gitCommitGraphKind,
  gitCommitOwnershipBadges,
  gitCommitTopologyLabel,
  gitRefLabels,
  type GitCommitGraphKind,
  type GitCommitOwnershipBadge
} from '../../sourceData.ts';
import type { GitCommitHistoryEntry, GitRepositorySummary } from '../../tauriSource.ts';

export type GitGraphDirtyTone = 'clean' | 'dirty' | 'error';
export type GitGraphSyncTone = 'clean' | 'ahead' | 'behind' | 'diverged' | 'no-upstream' | 'error';
export type GitGraphParentHintKind = 'linear' | 'merge' | 'root';

export type GitGraphTaskReference = {
  id: string;
  label: string;
  searchTarget: GitTaskSearchTarget;
};

export type GitGraphDirtySummary = {
  isDirty: boolean;
  dirtyCount: number;
  stagedCount: number;
  unstagedCount: number;
  untrackedCount: number;
  label: string;
  countLabel: string;
  detailLabel: string;
  tone: GitGraphDirtyTone;
  dirtySinceEpochMs: number | null;
  dirtyAgeMs: number | null;
  dirtyAgeLabel: string | null;
};

export type GitGraphSyncSummary = {
  ahead: number;
  behind: number;
  hasUpstream: boolean;
  label: string;
  detailLabel: string;
  tone: GitGraphSyncTone;
};

export type GitGraphParentHint = {
  kind: GitGraphParentHintKind;
  label: string;
  parentCount: number;
  parentShas: string[];
  firstParentSha: string | null;
  isMerge: boolean;
};

export type GitGraphRefSummary = {
  labels: string[];
  headLabels: string[];
  branchLabels: string[];
  remoteLabels: string[];
  tagLabels: string[];
  label: string;
};

export type GitGraphRepositoryRow = {
  kind: 'repository';
  id: string;
  projectID: string;
  projectName: string;
  repo: string;
  path: string;
  rootLabel: string;
  branch: string;
  branchLabel: string;
  headLabel: string;
  taskID: string | null;
  taskReferences: GitGraphTaskReference[];
  taskSearchTargets: GitTaskSearchTarget[];
  dirty: GitGraphDirtySummary;
  sync: GitGraphSyncSummary;
  isWorktree: boolean;
  lastCommitSha: string | null;
  lastCommitSubject: string | null;
  lastCommitAt: string | null;
  error: string | null;
  compactLabel: string;
  metaLabel: string;
  detailLabel: string;
  searchText: string;
};

export type GitGraphCommitRow = {
  kind: 'commit';
  id: string;
  index: number;
  sha: string;
  shortSha: string;
  subject: string;
  author: string;
  committedAt: string;
  refs: GitGraphRefSummary;
  graphKind: GitCommitGraphKind;
  topologyLabel: string;
  parentHint: GitGraphParentHint;
  taskID: string | null;
  taskSource: string | null;
  taskReferences: GitGraphTaskReference[];
  taskSearchTargets: GitTaskSearchTarget[];
  ownershipBadges: GitCommitOwnershipBadge[];
  compactLabel: string;
  metaLabel: string;
  detailLabel: string;
  searchText: string;
};

export type GitGraphViewModelInput = {
  repositories?: readonly GitRepositorySummary[];
  commits?: readonly GitCommitHistoryEntry[];
};

export type GitGraphViewModelOptions = {
  now?: number | null;
};

export type GitGraphViewModel = {
  repositories: GitGraphRepositoryRow[];
  commits: GitGraphCommitRow[];
  taskIDs: string[];
  taskSearchTargets: GitTaskSearchTarget[];
  summary: {
    repositoryCount: number;
    dirtyRepositoryCount: number;
    commitCount: number;
    mergeCommitCount: number;
    taskCount: number;
  };
};

export function buildGitGraphViewModel(
  input: GitGraphViewModelInput,
  options: GitGraphViewModelOptions = {}
): GitGraphViewModel {
  const repositories = buildGitRepositoryGraphRows(input.repositories ?? [], options);
  const commits = buildGitCommitGraphRows(input.commits ?? []);
  const taskIDs = uniqueTaskIDs([
    ...repositories.flatMap((row) => row.taskReferences.map((task) => task.id)),
    ...commits.flatMap((row) => row.taskReferences.map((task) => task.id))
  ]);
  const taskSearchTargets = taskIDs.map((taskID) => taskReference(taskID).searchTarget);

  return {
    repositories,
    commits,
    taskIDs,
    taskSearchTargets,
    summary: {
      repositoryCount: repositories.length,
      dirtyRepositoryCount: repositories.filter((row) => row.dirty.isDirty || row.error).length,
      commitCount: commits.length,
      mergeCommitCount: commits.filter((row) => row.parentHint.isMerge).length,
      taskCount: taskIDs.length
    }
  };
}

export function buildGitRepositoryGraphRows(
  summaries: readonly GitRepositorySummary[],
  options: GitGraphViewModelOptions = {}
): GitGraphRepositoryRow[] {
  return summaries.map((summary) => {
    const taskReferences = taskReferencesFromText(summary.taskID, [
      summary.branch,
      summary.rootLabel,
      summary.lastCommitSubject
    ]);
    const taskID = taskReferences[0]?.id ?? null;
    const dirty = gitRepositoryDirtySummary(summary, options);
    const sync = gitRepositorySyncSummary(summary);
    const branchLabel = summary.branch.trim() || 'unknown branch';
    const headLabel = summary.lastCommitSha?.trim() || 'unknown head';
    const compactParts = [summary.repo, branchLabel, dirty.label, sync.label].filter(Boolean);
    const metaParts = [
      summary.rootLabel,
      taskID,
      summary.isWorktree ? 'worktree' : 'main checkout',
      summary.lastCommitSha
    ].filter(Boolean);
    const detailParts = [
      summary.path,
      summary.lastCommitSubject ? `Last commit: ${summary.lastCommitSubject}` : '',
      summary.error ? `Error: ${summary.error}` : ''
    ].filter(Boolean);

    return {
      kind: 'repository',
      id: `repository:${summary.path}`,
      projectID: summary.projectID,
      projectName: summary.projectName,
      repo: summary.repo,
      path: summary.path,
      rootLabel: summary.rootLabel,
      branch: summary.branch,
      branchLabel,
      headLabel,
      taskID,
      taskReferences,
      taskSearchTargets: taskReferences.map((task) => task.searchTarget),
      dirty,
      sync,
      isWorktree: summary.isWorktree,
      lastCommitSha: summary.lastCommitSha,
      lastCommitSubject: summary.lastCommitSubject,
      lastCommitAt: summary.lastCommitAt,
      error: summary.error,
      compactLabel: compactParts.join(' · '),
      metaLabel: metaParts.join(' · '),
      detailLabel: detailParts.join('\n'),
      searchText: [
        summary.projectName,
        summary.repo,
        summary.path,
        summary.rootLabel,
        summary.branch,
        summary.lastCommitSha,
        summary.lastCommitSubject,
        summary.error,
        ...taskReferences.map((task) => task.id)
      ].filter(Boolean).join(' ')
    };
  });
}

export function buildGitCommitGraphRows(
  entries: readonly GitCommitHistoryEntry[]
): GitGraphCommitRow[] {
  return entries.map((entry, index) => {
    const parentHint = gitCommitParentHint(entry);
    const refs = gitCommitRefSummary(entry.refs);
    const taskReferences = taskReferencesFromText(entry.taskID, [entry.refs, entry.subject]);
    const taskID = taskReferences[0]?.id ?? null;
    const graphKind = gitCommitGraphKind(entry.refs, index, parentHint.parentCount);
    const topologyLabel = gitCommitTopologyLabel(entry.refs, index, parentHint.parentCount);
    const ownershipBadges = gitCommitOwnershipBadges({
      refs: entry.refs,
      taskID,
      taskSource: entry.taskSource,
      parentCount: parentHint.parentCount
    });
    const metaParts = [
      entry.shortSha,
      topologyLabel,
      parentHint.label,
      refs.label,
      taskID,
      entry.author,
      entry.committedAt
    ].filter(Boolean);
    const detailParts = [
      entry.sha,
      entry.subject,
      refs.label,
      parentHint.label,
      taskID ? `Task: ${taskID}${entry.taskSource ? ` from ${entry.taskSource}` : ''}` : '',
      entry.author,
      entry.committedAt
    ].filter(Boolean);

    return {
      kind: 'commit',
      id: `commit:${entry.sha}`,
      index,
      sha: entry.sha,
      shortSha: entry.shortSha,
      subject: entry.subject,
      author: entry.author,
      committedAt: entry.committedAt,
      refs,
      graphKind,
      topologyLabel,
      parentHint,
      taskID,
      taskSource: entry.taskSource,
      taskReferences,
      taskSearchTargets: taskReferences.map((task) => task.searchTarget),
      ownershipBadges,
      compactLabel: `${entry.shortSha} · ${entry.subject}`,
      metaLabel: metaParts.join(' · '),
      detailLabel: detailParts.join('\n'),
      searchText: [
        entry.shortSha,
        entry.sha,
        entry.subject,
        entry.author,
        entry.refs,
        parentHint.label,
        ...entry.parentShas,
        ...taskReferences.map((task) => task.id)
      ].filter(Boolean).join(' ')
    };
  });
}

export function gitRepositoryDirtySummary(
  summary: GitRepositorySummary,
  options: GitGraphViewModelOptions = {}
): GitGraphDirtySummary {
  const stagedCount = safeCount(summary.stagedCount);
  const unstagedCount = safeCount(summary.unstagedCount);
  const untrackedCount = safeCount(summary.untrackedCount);
  const dirtyCount = safeCount(summary.dirtyCount);
  const dirtySinceEpochMs = safeEpochMs(summary.dirtySinceEpochMs);
  const now = safeEpochMs(options.now);
  const dirtyAgeMs = dirtySinceEpochMs !== null && now !== null ? Math.max(0, now - dirtySinceEpochMs) : null;
  const dirtyAgeLabel = dirtyAgeMs !== null ? formatCompactAge(dirtyAgeMs) : null;
  const hasDirtyCounts = stagedCount > 0 || unstagedCount > 0 || untrackedCount > 0 || dirtyCount > 0;
  const isDirty = Boolean(summary.isDirty || hasDirtyCounts);

  if (summary.error) {
    return {
      isDirty,
      dirtyCount,
      stagedCount,
      unstagedCount,
      untrackedCount,
      label: 'error',
      countLabel: formatChangedCount(dirtyCount),
      detailLabel: summary.error,
      tone: 'error',
      dirtySinceEpochMs,
      dirtyAgeMs,
      dirtyAgeLabel
    };
  }

  if (!isDirty) {
    return {
      isDirty: false,
      dirtyCount: 0,
      stagedCount: 0,
      unstagedCount: 0,
      untrackedCount: 0,
      label: 'clean',
      countLabel: 'clean',
      detailLabel: 'clean',
      tone: 'clean',
      dirtySinceEpochMs,
      dirtyAgeMs,
      dirtyAgeLabel
    };
  }

  const detailParts = [
    stagedCount > 0 ? `staged ${stagedCount}` : '',
    unstagedCount > 0 ? `unstaged ${unstagedCount}` : '',
    untrackedCount > 0 ? `untracked ${untrackedCount}` : ''
  ].filter(Boolean);
  const countLabel = formatChangedCount(dirtyCount || stagedCount + unstagedCount + untrackedCount);
  const label = [countLabel, detailParts.join(' / '), dirtyAgeLabel].filter(Boolean).join(' · ');

  return {
    isDirty,
    dirtyCount,
    stagedCount,
    unstagedCount,
    untrackedCount,
    label,
    countLabel,
    detailLabel: detailParts.length > 0 ? detailParts.join(' / ') : countLabel,
    tone: 'dirty',
    dirtySinceEpochMs,
    dirtyAgeMs,
    dirtyAgeLabel
  };
}

export function gitRepositorySyncSummary(summary: GitRepositorySummary): GitGraphSyncSummary {
  const ahead = safeCount(summary.ahead);
  const behind = safeCount(summary.behind);
  const hasUpstream = Boolean(summary.hasUpstream);

  if (summary.error) {
    return {
      ahead,
      behind,
      hasUpstream,
      label: 'sync unknown',
      detailLabel: summary.error,
      tone: 'error'
    };
  }

  if (!hasUpstream) {
    const fallbackLabel = ahead > 0 || behind > 0 ? syncCountLabel(ahead, behind) : 'no upstream';
    return {
      ahead,
      behind,
      hasUpstream,
      label: fallbackLabel,
      detailLabel: 'no upstream configured',
      tone: 'no-upstream'
    };
  }

  if (ahead > 0 && behind > 0) {
    return {
      ahead,
      behind,
      hasUpstream,
      label: syncCountLabel(ahead, behind),
      detailLabel: 'local and upstream both have unique commits',
      tone: 'diverged'
    };
  }

  if (ahead > 0) {
    return {
      ahead,
      behind,
      hasUpstream,
      label: `ahead ${ahead}`,
      detailLabel: `${ahead} local ${ahead === 1 ? 'commit' : 'commits'} not pushed`,
      tone: 'ahead'
    };
  }

  if (behind > 0) {
    return {
      ahead,
      behind,
      hasUpstream,
      label: `behind ${behind}`,
      detailLabel: `${behind} upstream ${behind === 1 ? 'commit' : 'commits'} not pulled`,
      tone: 'behind'
    };
  }

  return {
    ahead,
    behind,
    hasUpstream,
    label: 'up to date',
    detailLabel: 'up to date',
    tone: 'clean'
  };
}

export function gitCommitParentHint(entry: GitCommitHistoryEntry): GitGraphParentHint {
  const parentShas = Array.isArray(entry.parentShas)
    ? entry.parentShas.map((parent) => parent.trim()).filter(Boolean)
    : [];
  const parentCount = safeCount(Number.isFinite(entry.parentCount) ? entry.parentCount : parentShas.length);
  const firstParentSha = parentShas[0] ?? null;

  if (parentCount > 1) {
    return {
      kind: 'merge',
      label: `${parentCount} parents`,
      parentCount,
      parentShas,
      firstParentSha,
      isMerge: true
    };
  }

  if (parentCount === 0) {
    return {
      kind: 'root',
      label: 'root commit',
      parentCount,
      parentShas,
      firstParentSha,
      isMerge: false
    };
  }

  return {
    kind: 'linear',
    label: firstParentSha ? `parent ${shortenSha(firstParentSha)}` : 'linear',
    parentCount,
    parentShas,
    firstParentSha,
    isMerge: false
  };
}

export function gitCommitRefSummary(refs: string): GitGraphRefSummary {
  const labels = gitRefLabels(refs);
  const headLabels = labels.filter((label) => label === 'HEAD' || label.startsWith('HEAD ->'));
  const tagLabels = labels.filter((label) => label.startsWith('tag:'));
  const remoteLabels = labels.filter(isRemoteRefLabel);
  const branchLabels = labels
    .map(branchLabelFromRef)
    .filter((label): label is string => Boolean(label));

  return {
    labels,
    headLabels,
    branchLabels,
    remoteLabels,
    tagLabels,
    label: labels.length > 0 ? labels.join(', ') : 'no refs'
  };
}

function taskReferencesFromText(
  primaryTaskID: string | null | undefined,
  textSources: readonly (string | null | undefined)[]
): GitGraphTaskReference[] {
  const taskIDs = uniqueTaskIDs([
    ...taskIDsFromText(primaryTaskID),
    ...textSources.flatMap((source) => taskIDsFromText(source))
  ]);

  return taskIDs.map(taskReference);
}

function taskIDsFromText(input: string | null | undefined): string[] {
  const normalized = normalizeGitTaskID(input);
  return normalized ? [normalized] : extractGitTaskIDs(input);
}

function taskReference(taskID: string): GitGraphTaskReference {
  const searchTarget = buildGitTaskSearchTarget(taskID);
  return {
    id: taskID,
    label: taskID,
    searchTarget: searchTarget ?? {
      kind: 'notion-search',
      id: taskID,
      label: taskID,
      query: taskID
    }
  };
}

function uniqueTaskIDs(taskIDs: readonly string[]): string[] {
  const seen = new Set<string>();
  const unique: string[] = [];

  for (const taskID of taskIDs) {
    const normalizedTaskID = normalizeGitTaskID(taskID);
    if (!normalizedTaskID || seen.has(normalizedTaskID)) continue;

    seen.add(normalizedTaskID);
    unique.push(normalizedTaskID);
  }

  return unique;
}

function safeCount(value: number | null | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.max(0, Math.floor(value)) : 0;
}

function safeEpochMs(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? Math.max(0, Math.floor(value)) : null;
}

function formatChangedCount(count: number): string {
  return `${count} changed`;
}

function syncCountLabel(ahead: number, behind: number): string {
  return `ahead ${ahead} / behind ${behind}`;
}

function formatCompactAge(elapsedMs: number): string {
  const minuteMs = 60 * 1000;
  const hourMs = 60 * minuteMs;
  const dayMs = 24 * hourMs;

  if (elapsedMs < minuteMs) return 'just now';
  if (elapsedMs < hourMs) return `${Math.floor(elapsedMs / minuteMs)}m`;
  if (elapsedMs < dayMs) return `${Math.floor(elapsedMs / hourMs)}h`;
  return `${Math.floor(elapsedMs / dayMs)}d`;
}

function shortenSha(sha: string): string {
  return sha.slice(0, 7);
}

function branchLabelFromRef(label: string): string | null {
  if (!label) return null;
  if (label.startsWith('HEAD ->')) return label.replace(/^HEAD ->\s*/, '').trim() || null;
  if (label === 'HEAD' || label.startsWith('tag:') || isRemoteRefLabel(label)) return null;
  return label;
}

function isRemoteRefLabel(label: string): boolean {
  if (!label || label === 'HEAD' || label.startsWith('HEAD ->') || label.startsWith('tag:')) {
    return false;
  }

  if (label.startsWith('refs/')) return false;
  return /^[A-Za-z0-9_.-]+\/.+/.test(label);
}
