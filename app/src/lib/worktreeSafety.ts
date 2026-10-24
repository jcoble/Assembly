import type { ProjectWorktree } from './tauriSource';
import { extractGitTaskIDs } from './shell/git/gitTaskLinks.ts';

export type WorktreeSafetyKind = 'protected' | 'blocked' | 'ready' | 'review';
export type WorktreeAgeBucket = 'active' | 'stale' | 'unknown';

export type WorktreeSafetySummary = {
  kind: WorktreeSafetyKind;
  ageBucket: WorktreeAgeBucket;
  activeSessionCount: number;
  savedWorkspaceCount: number;
  badge: string;
  reason: string;
  recommendation: string;
  activityLabel: string;
  auditCommand: string;
  backupCommand: string;
  cleanupCommand: string;
  cleanupPlan: string;
  decisionChecklist: string[];
};

export type WorktreePrimaryActionKind = 'audit' | 'backup' | 'cleanup';
export type WorktreeDecisionLaneTone = 'blocked' | 'backup' | 'cleanup' | 'review' | 'protected';

export type WorktreeDecisionLane = {
  label: string;
  detail: string;
  tone: WorktreeDecisionLaneTone;
};

export type WorktreePrimaryAction = {
  kind: WorktreePrimaryActionKind;
  label: string;
  title: string;
  command: string;
  clipboardMessage: string;
};

export type WorktreeCleanupBrief = {
  headline: string;
  cleanupCandidateCount: number;
  blockedCount: number;
  reviewCount: number;
  protectedCount: number;
  staleCount: number;
  taskIDs: string[];
  report: string;
};

export type WorktreeDecisionQueueID = 'blocked' | 'ready' | 'review' | 'protected';

export type WorktreeDecisionQueueEntry = {
  worktree: ProjectWorktree;
  safety: WorktreeSafetySummary;
  primaryAction: WorktreePrimaryAction;
};

export type WorktreeDecisionQueueGroup = {
  id: WorktreeDecisionQueueID;
  label: string;
  summary: string;
  actionLabel: string;
  entries: WorktreeDecisionQueueEntry[];
};

export type WorktreeTaskGroup = {
  taskID: string | null;
  label: string;
  summary: string;
  worktreeCount: number;
  cleanupCandidateCount: number;
  blockedCount: number;
  reviewCount: number;
  protectedCount: number;
  staleCount: number;
  activeSessionCount: number;
  savedWorkspaceCount: number;
  needsBackupCount: number;
  requiresManualSignoff: boolean;
  primaryAction: WorktreePrimaryAction;
  entries: WorktreeDecisionQueueEntry[];
};

export type WorktreeSafetyOptions = {
  primaryPath?: string | null;
  activeSessionPaths?: Array<string | null | undefined>;
  savedWorkspacePaths?: Array<string | null | undefined>;
  now?: number | Date;
  staleAfterDays?: number;
};

const defaultStaleAfterDays = 14;

export function buildWorktreeSafetySummary(
  worktree: ProjectWorktree,
  options: WorktreeSafetyOptions = {}
): WorktreeSafetySummary {
  const nowMs = normalizeNow(options.now);
  const staleAfterDays = options.staleAfterDays ?? defaultStaleAfterDays;
  const activity = worktreeActivity(worktree.lastActivity, nowMs, staleAfterDays);
  const primaryPath = options.primaryPath ? normalizePath(options.primaryPath) : '';
  const worktreePath = normalizePath(worktree.path);
  const isPrimaryCheckout = Boolean(primaryPath && primaryPath === worktreePath);
  const activeSessionCount = countOwnedPaths(worktreePath, options.activeSessionPaths);
  const savedWorkspaceCount = countOwnedPaths(worktreePath, options.savedWorkspacePaths);
  const deleteEligibility = worktree.deleteEligibility.toLowerCase();
  const isPrunable = worktreeIsPrunable(worktree);
  const isLocked = worktreeIsLocked(worktree);
  const isUntrackedOnly = worktreeHasUntrackedFiles(worktree) && !worktree.isDirty;
  const isDirty = worktreeIsDirty(worktree);
  const hasUnmergedCommits = worktreeHasUnmergedCommits(worktree);

  let kind: WorktreeSafetyKind = 'ready';
  let badge = 'Safe';
  let reason = 'Clean worktree';
  let recommendation = 'Review branch ownership, then remove from the main checkout when finished.';

  if (isPrimaryCheckout) {
    kind = 'protected';
    badge = 'Main';
    reason = 'Primary checkout';
    recommendation = 'Keep this worktree as the repo anchor; clean branches from sibling worktrees instead.';
  } else if (activeSessionCount > 0) {
    kind = 'blocked';
    badge = 'Active';
    reason = `Active ${activeSessionCount === 1 ? 'session' : 'sessions'} owns this worktree`;
    recommendation = 'Resume, close, or move active agent sessions before cleanup.';
  } else if (isUntrackedOnly) {
    kind = 'blocked';
    badge = 'Untracked';
    reason = 'Untracked files';
    recommendation = 'Archive, commit, or intentionally discard untracked files before cleanup.';
  } else if (isDirty) {
    kind = 'blocked';
    badge = 'Dirty';
    reason = 'Uncommitted changes';
    recommendation = 'Open it, review git status, then archive, commit, or stash before cleanup.';
  } else if (hasUnmergedCommits) {
    kind = 'blocked';
    badge = 'Unmerged';
    reason = 'Local commits not on a remote branch';
    recommendation = 'Inspect the commit list and merge, push, cherry-pick, or intentionally archive before cleanup.';
  } else if (isLocked) {
    kind = 'blocked';
    badge = 'Locked';
    reason = 'Locked worktree';
    recommendation = 'Audit the lock reason and unlock intentionally before cleanup.';
  } else if (isPrunable) {
    kind = 'review';
    badge = 'Missing';
    reason = 'Missing worktree path';
    recommendation = 'Confirm the folder is intentionally gone, then prune stale Git worktree metadata from the main checkout.';
  } else if (savedWorkspaceCount > 0) {
    kind = 'review';
    badge = 'Workspace';
    reason = `Saved workspace ${savedWorkspaceCount === 1 ? 'snapshot' : 'snapshots'} point here`;
    recommendation = 'Restore, move, or delete saved workspace snapshots before removing this worktree.';
  } else if (activity.ageBucket === 'stale') {
    kind = 'ready';
    badge = 'Stale';
    reason = 'Clean and inactive';
    recommendation = 'Good cleanup candidate after confirming no active agent owns it.';
  } else if (deleteEligibility.includes('confirmation') || deleteEligibility.includes('unknown')) {
    kind = 'review';
    badge = 'Review';
    reason = 'Clean, needs confirmation';
    recommendation = 'Confirm no session owns it, then remove it from the main checkout.';
  }

  const auditCommand = isPrunable
    ? worktreePrunableAuditCommand(options.primaryPath)
    : worktreeAuditCommand(worktree);
  const backupCommand = worktreeBackupCommand(worktree, nowMs);
  const cleanupCommand = worktreeCleanupCommand(worktree, options.primaryPath);
  const decisionChecklist = worktreeDecisionChecklist({
    kind,
    badge,
      reason,
      ageBucket: activity.ageBucket,
      isPrimaryCheckout,
      activeSessionCount,
      savedWorkspaceCount
    });

  return {
    kind,
    ageBucket: activity.ageBucket,
    activeSessionCount,
    savedWorkspaceCount,
    badge,
    reason,
    recommendation,
    activityLabel: activity.label,
    auditCommand,
    backupCommand,
    cleanupCommand,
    cleanupPlan: worktreeCleanupPlan(worktree, {
      auditCommand,
      backupCommand,
      cleanupCommand,
      reason,
      recommendation,
      isPrimaryCheckout,
      activeSessionCount,
      savedWorkspaceCount,
      isPrunable,
      isLocked,
      isDirty,
      hasUnmergedCommits,
      decisionChecklist
    }),
    decisionChecklist
  };
}

export function buildWorktreeCleanupBrief(
  worktrees: ProjectWorktree[],
  options: WorktreeSafetyOptions = {}
): WorktreeCleanupBrief {
  const entries = worktrees.map((worktree) => ({
    worktree,
    safety: buildWorktreeSafetySummary(worktree, options)
  }));
  const blocked = entries.filter((entry) => entry.safety.kind === 'blocked');
  const ready = entries.filter((entry) => entry.safety.kind === 'ready');
  const review = entries.filter((entry) => entry.safety.kind === 'review');
  const protectedEntries = entries.filter((entry) => entry.safety.kind === 'protected');
  const stale = entries.filter((entry) => entry.safety.ageBucket === 'stale');
  const taskIDs = uniqueTaskIDs(entries.map((entry) => worktreeTaskID(entry.worktree)));
  const headline = formatBriefHeadline({
    blocked: blocked.length,
    ready: ready.length,
    review: review.length,
    stale: stale.length,
    protected: protectedEntries.length
  });

  return {
    headline,
    cleanupCandidateCount: ready.length,
    blockedCount: blocked.length,
    reviewCount: review.length,
    protectedCount: protectedEntries.length,
    staleCount: stale.length,
    taskIDs,
    report: formatCleanupBriefReport({
      headline,
      taskIDs,
      blocked,
      ready,
      review,
      protectedEntries
    })
  };
}

export function prioritizeWorktreesForCleanup(
  worktrees: ProjectWorktree[],
  options: WorktreeSafetyOptions = {}
): ProjectWorktree[] {
  return worktrees
    .map((worktree, index) => ({
      worktree,
      safety: buildWorktreeSafetySummary(worktree, options),
      index
    }))
    .sort((left, right) => {
      const priorityDelta = worktreeCleanupPriority(left.safety) - worktreeCleanupPriority(right.safety);
      if (priorityDelta) return priorityDelta;

      const activityDelta = worktreeActivitySortValue(left.worktree) - worktreeActivitySortValue(right.worktree);
      if (activityDelta) return activityDelta;

      const branchDelta = left.worktree.branch.localeCompare(right.worktree.branch);
      if (branchDelta) return branchDelta;

      return left.index - right.index;
    })
    .map((entry) => entry.worktree);
}

export function buildWorktreeDecisionQueue(
  worktrees: ProjectWorktree[],
  options: WorktreeSafetyOptions = {}
): WorktreeDecisionQueueGroup[] {
  const groups = createDecisionQueueGroups();

  for (const worktree of prioritizeWorktreesForCleanup(worktrees, options)) {
    const safety = buildWorktreeSafetySummary(worktree, options);
    const group = groups.find((candidateGroup) => candidateGroup.id === worktreeDecisionQueueID(safety));
    if (!group) continue;

    group.entries.push({
      worktree,
      safety,
      primaryAction: worktreePrimaryAction(safety)
    });
  }

  return groups
    .filter((group) => group.entries.length > 0)
    .map((group) => ({
      ...group,
      summary: formatDecisionQueueGroupSummary(group.entries)
    }));
}

export function buildWorktreeTaskGroups(
  worktrees: ProjectWorktree[],
  options: WorktreeSafetyOptions = {}
): WorktreeTaskGroup[] {
  const groups = new Map<string, WorktreeDecisionQueueEntry[]>();

  for (const worktree of prioritizeWorktreesForCleanup(worktrees, options)) {
    const taskID = worktreeTaskID(worktree);
    const key = taskID ?? '__no_task__';
    const safety = buildWorktreeSafetySummary(worktree, options);
    const entries = groups.get(key) ?? [];

    entries.push({
      worktree,
      safety,
      primaryAction: worktreePrimaryAction(safety)
    });
    groups.set(key, entries);
  }

  return Array.from(groups.entries())
    .map(([key, entries]) => formatWorktreeTaskGroup(key === '__no_task__' ? null : key, entries))
    .sort((left, right) => {
      const priorityDelta = worktreeTaskGroupPriority(left) - worktreeTaskGroupPriority(right);
      if (priorityDelta) return priorityDelta;

      return left.label.localeCompare(right.label);
    });
}

export function buildWorktreeCleanupScript(
  worktrees: ProjectWorktree[],
  options: WorktreeSafetyOptions = {}
): string {
  const entries = worktrees.map((worktree) => ({
    worktree,
    safety: buildWorktreeSafetySummary(worktree, options)
  }));
  const brief = buildWorktreeCleanupBrief(worktrees, options);
  const lines = [
    '#!/usr/bin/env bash',
    'set -euo pipefail',
    '',
    'RUN_BACKUP="${RUN_BACKUP:-0}"',
    'RUN_REMOVE="${RUN_REMOVE:-0}"',
    '',
    `echo ${shellQuote(`Worktree cleanup: ${brief.headline}`)}`,
    ''
  ];

  for (const { worktree, safety } of entries) {
    const branch = worktree.branch || worktree.path;
    lines.push(`# ${branch} - ${safety.reason}`);
    lines.push(`echo ${shellQuote(`Audit ${branch}`)}`);
    lines.push(safety.auditCommand);

    if (worktreeIsPrunable(worktree)) {
      lines.push('if [ "$RUN_REMOVE" = "1" ]; then');
      lines.push(`  echo ${shellQuote(`Prune missing worktree metadata for ${branch}`)}`);
      lines.push(`  ${safety.cleanupCommand}`);
      lines.push('else');
      lines.push(`  echo ${shellQuote(`Prune ${branch}: set RUN_REMOVE=1 to execute metadata cleanup`)}`);
      lines.push(`  printf '%s\\n' ${shellQuote(safety.cleanupCommand)}`);
      lines.push('fi');
    } else if (safety.kind === 'blocked' && safety.activeSessionCount === 0 && safety.badge !== 'Locked') {
      lines.push('if [ "$RUN_BACKUP" = "1" ]; then');
      lines.push(`  echo ${shellQuote(`Backup ${branch}`)}`);
      lines.push(`  ${safety.backupCommand}`);
      lines.push('else');
      lines.push(`  echo ${shellQuote(`Backup ${branch}: set RUN_BACKUP=1 to execute`)}`);
      lines.push(`  printf '%s\\n' ${shellQuote(safety.backupCommand)}`);
      lines.push('fi');
      lines.push(`echo ${shellQuote('Forced dirty/unmerged worktree removal is intentionally not generated. Backup, commit, stash, or archive first.')}`);
    } else if (safety.kind === 'ready') {
      lines.push('if [ "$RUN_REMOVE" = "1" ]; then');
      lines.push(`  echo ${shellQuote(`Remove ${branch}`)}`);
      lines.push(`  ${safety.cleanupCommand}`);
      lines.push('else');
      lines.push(`  echo ${shellQuote(`Remove ${branch}: set RUN_REMOVE=1 to execute`)}`);
      lines.push(`  printf '%s\\n' ${shellQuote(safety.cleanupCommand)}`);
      lines.push('fi');
    } else {
      lines.push(`echo ${shellQuote(`Review ${branch}: ${safety.recommendation}`)}`);
    }

    lines.push('');
  }

  lines.push('echo "Dry run complete. Set RUN_BACKUP=1 and/or RUN_REMOVE=1 to execute guarded actions."');
  return `${lines.join('\n')}\n`;
}

function createDecisionQueueGroups(): WorktreeDecisionQueueGroup[] {
  return [
    {
      id: 'blocked',
      label: 'Needs decision',
      summary: '',
      actionLabel: 'Audit or backup before cleanup',
      entries: []
    },
    {
      id: 'ready',
      label: 'Cleanup ready',
      summary: '',
      actionLabel: 'Remove after final confirmation',
      entries: []
    },
    {
      id: 'review',
      label: 'Review',
      summary: '',
      actionLabel: 'Audit ownership',
      entries: []
    },
    {
      id: 'protected',
      label: 'Protected',
      summary: '',
      actionLabel: 'Keep as repo anchor',
      entries: []
    }
  ];
}

function worktreeDecisionQueueID(summary: WorktreeSafetySummary): WorktreeDecisionQueueID {
  if (summary.kind === 'blocked') return 'blocked';
  if (summary.kind === 'ready') return 'ready';
  if (summary.kind === 'review') return 'review';
  return 'protected';
}

export function worktreePrimaryAction(summary: WorktreeSafetySummary): WorktreePrimaryAction {
  if (summary.kind === 'ready') {
    return {
      kind: 'cleanup',
      label: 'Remove',
      title: 'Copy the safe remove command for this clean worktree.',
      command: summary.cleanupCommand,
      clipboardMessage: 'Recommended worktree remove command copied'
    };
  }

  if (summary.kind === 'blocked' && summary.activeSessionCount === 0 && summary.badge !== 'Locked') {
    return {
      kind: 'backup',
      label: 'Backup',
      title: 'Archive a recoverable backup before deciding whether to remove this worktree.',
      command: summary.backupCommand,
      clipboardMessage: 'Recommended worktree backup command copied'
    };
  }

  const activeReason = summary.activeSessionCount > 0 ? ' while active sessions still point here' : '';
  return {
    kind: 'audit',
    label: 'Audit',
    title: `Copy the audit command before changing this worktree${activeReason}.`,
    command: summary.auditCommand,
    clipboardMessage: 'Recommended worktree audit command copied'
  };
}

export function worktreeDecisionLane(summary: WorktreeSafetySummary): WorktreeDecisionLane {
  if (summary.kind === 'protected') {
    return {
      label: 'Keep',
      detail: 'Primary checkout; keep it as the repo anchor.',
      tone: 'protected'
    };
  }

  if (summary.activeSessionCount > 0) {
    return {
      label: 'Active',
      detail: 'Active sessions point at this worktree; resume or close them before cleanup.',
      tone: 'blocked'
    };
  }

  if (summary.badge === 'Dirty') {
    return {
      label: 'Backup',
      detail: 'Uncommitted changes need archive, commit, or stash before cleanup.',
      tone: 'backup'
    };
  }

  if (summary.badge === 'Untracked') {
    return {
      label: 'Backup files',
      detail: 'Untracked files need archive, commit, or intentional discard before cleanup.',
      tone: 'backup'
    };
  }

  if (summary.badge === 'Unmerged') {
    return {
      label: 'Save commits',
      detail: 'Local commits need push, merge, cherry-pick, or archive before cleanup.',
      tone: 'backup'
    };
  }

  if (summary.badge === 'Locked') {
    return {
      label: 'Locked',
      detail: 'Audit and intentionally unlock this worktree outside the app before cleanup.',
      tone: 'blocked'
    };
  }

  if (summary.badge === 'Missing') {
    return {
      label: 'Prune',
      detail: 'Missing path; confirm it is gone, then prune metadata from the main checkout.',
      tone: 'review'
    };
  }

  if (summary.badge === 'Workspace') {
    return {
      label: 'Workspace',
      detail: 'Saved workspace snapshots point at this worktree; clear ownership before cleanup.',
      tone: 'review'
    };
  }

  if (summary.kind === 'ready') {
    return {
      label: summary.ageBucket === 'stale' ? 'Stale clean' : 'Clean',
      detail: 'Clean worktree; remove after confirming ownership and task status.',
      tone: 'cleanup'
    };
  }

  return {
    label: 'Review',
    detail: 'Confirm ownership, task status, and active sessions before cleanup.',
    tone: 'review'
  };
}

export function worktreeAuditCommand(worktree: ProjectWorktree): string {
  return [
    'git',
    '-C',
    shellQuote(worktree.path),
    'status',
    '--short',
    '--branch',
    '&&',
    'git',
    '-C',
    shellQuote(worktree.path),
    'branch',
    '-vv',
    '&&',
    'git',
    '-C',
    shellQuote(worktree.path),
    'log',
    '--oneline',
    '--decorate',
    '--max-count=8',
    '&&',
    'git',
    '-C',
    shellQuote(worktree.path),
    'log',
    '--branches',
    '--not',
    '--remotes',
    '--oneline',
    '--decorate',
    '--max-count=20'
  ].join(' ');
}

export function worktreeBackupCommand(worktree: ProjectWorktree, now: number | Date = Date.now()): string {
  if (worktreeIsPrunable(worktree)) {
    return '# Missing/prunable worktree path has no files to archive. Confirm it is intentionally gone, then prune metadata from the main checkout.';
  }

  const nowMs = normalizeNow(now);
  const timestamp = backupTimestamp(nowMs);
  const archiveDirectory = worktreeArchiveDirectory(worktree, nowMs);
  const gitInWorktree = ['git', '-C', shellQuote(worktree.path)].join(' ');

  return [
    `mkdir -p ${shellQuote(archiveDirectory)}`,
    `${gitInWorktree} status --short --branch > ${shellQuote(`${archiveDirectory}/status.txt`)}`,
    `${gitInWorktree} log --oneline --decorate --max-count=40 > ${shellQuote(`${archiveDirectory}/commits.txt`)}`,
    `${gitInWorktree} diff --binary > ${shellQuote(`${archiveDirectory}/unstaged.patch`)}`,
    `${gitInWorktree} diff --cached --binary > ${shellQuote(`${archiveDirectory}/staged.patch`)}`,
    `${gitInWorktree} ls-files --others --exclude-standard > ${shellQuote(`${archiveDirectory}/untracked.txt`)}`,
    `${gitInWorktree} bundle create ${shellQuote(`${archiveDirectory}/head.bundle`)} HEAD`,
    `${gitInWorktree} stash push --include-untracked -m ${shellQuote(`mcb backup ${worktree.branch} ${timestamp}`)}`
  ].join(' && ');
}

export function worktreeCleanupCommand(
  worktree: ProjectWorktree,
  primaryPath: string | null | undefined
): string {
  if (!primaryPath || normalizePath(primaryPath) === normalizePath(worktree.path)) {
    return '# Open the main checkout first, then run git worktree remove for this sibling path.';
  }

  if (worktreeIsPrunable(worktree)) {
    return ['git', '-C', shellQuote(primaryPath), 'worktree', 'prune'].join(' ');
  }

  return [
    'git',
    '-C',
    shellQuote(primaryPath),
    'worktree',
    'remove',
    shellQuote(worktree.path),
    '&&',
    'git',
    '-C',
    shellQuote(primaryPath),
    'worktree',
    'prune'
  ].join(' ');
}

function worktreeCleanupPlan(
  worktree: ProjectWorktree,
  details: {
    auditCommand: string;
    backupCommand: string;
    cleanupCommand: string;
    reason: string;
    recommendation: string;
    isPrimaryCheckout: boolean;
    activeSessionCount: number;
    savedWorkspaceCount: number;
    isPrunable: boolean;
    isLocked: boolean;
    isDirty: boolean;
    hasUnmergedCommits: boolean;
    decisionChecklist: string[];
  }
): string {
  const lines = [
    `Worktree: ${worktree.branch}`,
    `Path: ${worktree.path}`,
    `Repo: ${worktree.repo}`,
    worktreeTaskID(worktree) ? `Task: ${worktreeTaskID(worktree)}` : '',
    `State: ${details.reason}`,
    details.activeSessionCount > 0 ? `Active sessions: ${details.activeSessionCount}` : '',
    details.savedWorkspaceCount > 0 ? `Saved workspaces: ${details.savedWorkspaceCount}` : '',
    `Recommended: ${details.recommendation}`,
    ''
  ].filter(Boolean);

  lines.push('Decision checklist:');
  for (const item of details.decisionChecklist) {
    lines.push(`- ${item}`);
  }
  lines.push('');

  if (details.isPrimaryCheckout) {
    lines.push('Audit current state:');
    lines.push(details.auditCommand);
    lines.push('');
    lines.push('Do not remove the primary checkout from the worktree list.');
  } else if (details.isPrunable) {
    lines.push('Audit missing worktree metadata:');
    lines.push(details.auditCommand);
    lines.push('');
    lines.push('Metadata-only cleanup after confirming the folder is intentionally gone:');
    lines.push(details.cleanupCommand);
    lines.push('');
    lines.push('This prunes stale Git worktree metadata; it does not delete source files at the missing path.');
  } else {
    if (details.activeSessionCount > 0) {
      lines.push('Do not remove while active sessions point here. Resume, close, or move them first.');
      lines.push('');
    }
    if (details.isLocked) {
      lines.push('Do not remove locked worktrees from this app. Audit the lock, then unlock intentionally in Git if cleanup is still desired.');
      lines.push('');
    }
    if (details.savedWorkspaceCount > 0) {
      lines.push('Do not remove while saved workspace snapshots still point here. Restore, move, or delete those snapshots first.');
      lines.push('');
    }
    lines.push('Audit before cleanup:');
    lines.push(details.auditCommand);
    lines.push('');
    lines.push('Archive a recoverable backup before cleanup:');
    lines.push(details.backupCommand);
    lines.push('');
    lines.push('Remove once clean and no active session owns it:');
    lines.push(details.cleanupCommand);
    if (details.isDirty || details.hasUnmergedCommits) {
      lines.push('');
      lines.push('Forced removal is intentionally not generated for dirty or unmerged worktrees. Backup, commit, stash, or archive first; use a manual Git force remove only after reviewing recoverability.');
    }
  }

  return lines.join('\n');
}

function worktreeDecisionChecklist(details: {
  kind: WorktreeSafetyKind;
  badge: string;
  reason: string;
  ageBucket: WorktreeAgeBucket;
  isPrimaryCheckout: boolean;
  activeSessionCount: number;
  savedWorkspaceCount: number;
}): string[] {
  if (details.isPrimaryCheckout) {
    return [
      'Keep this checkout as the repository anchor.',
      'Clean sibling worktrees instead of removing main.'
    ];
  }

  if (details.activeSessionCount > 0) {
    return [
      'Resume or close the active sessions using this path.',
      'Refresh sessions and worktrees before attempting cleanup.'
    ];
  }

  if (details.badge === 'Dirty') {
    return [
      'Inspect git status and uncommitted files.',
      'Archive, commit, or stash the changes before removal.',
      'Remove only after the worktree is clean or intentionally backed up.'
    ];
  }

  if (details.badge === 'Untracked') {
    return [
      'Inspect untracked files before cleanup.',
      'Archive, commit, or intentionally discard them before removal.',
      'Remove only after those files are recoverable or confirmed unnecessary.'
    ];
  }

  if (details.badge === 'Unmerged') {
    return [
      'Inspect local commits that are not on a remote branch.',
      'Push, merge, cherry-pick, or archive the branch before removal.',
      'Remove only after the branch is recoverable from another ref.'
    ];
  }

  if (details.badge === 'Locked') {
    return [
      'Inspect the Git worktree lock reason.',
      'Unlock only when you know no external process owns this worktree.',
      'Refresh worktrees before attempting cleanup.'
    ];
  }

  if (details.badge === 'Missing') {
    return [
      'Confirm the path is intentionally gone and not a disconnected volume.',
      'Run a dry-run prune from the main checkout.',
      'Prune stale metadata only after confirmation.'
    ];
  }

  if (details.badge === 'Workspace') {
    return [
      'Review saved workspace snapshots that point at this path.',
      'Restore, move, or delete stale workspace snapshots before removal.',
      'Refresh worktrees after clearing saved workspace ownership.'
    ];
  }

  if (details.kind === 'ready' && details.ageBucket === 'stale') {
    return [
      'Confirm no active session owns this stale path.',
      'Remove from the main checkout, then prune worktree metadata.'
    ];
  }

  if (details.kind === 'ready') {
    return [
      'Confirm branch ownership and task status.',
      'Remove from the main checkout when no session needs it.'
    ];
  }

  return [
    `Audit before cleanup: ${details.reason}.`,
    'Confirm ownership, task status, and active sessions before removal.'
  ];
}

function formatBriefHeadline(counts: {
  blocked: number;
  ready: number;
  review: number;
  stale: number;
  protected: number;
}): string {
  const parts = [
    counts.blocked ? `${counts.blocked} blocked` : '',
    counts.ready ? `${counts.ready} ready` : '',
    counts.review ? `${counts.review} review` : '',
    counts.stale ? `${counts.stale} stale` : '',
    counts.protected ? `${counts.protected} main` : ''
  ].filter(Boolean);

  return parts.join(' · ') || 'no worktrees';
}

function worktreeCleanupPriority(safety: WorktreeSafetySummary): number {
  if (safety.kind === 'blocked' && safety.activeSessionCount > 0) return 0;
  if (safety.kind === 'blocked' && safety.badge === 'Dirty') return 1;
  if (safety.kind === 'blocked' && safety.badge === 'Untracked') return 2;
  if (safety.kind === 'blocked' && safety.badge === 'Unmerged') return 3;
  if (safety.kind === 'blocked' && safety.badge === 'Locked') return 4;
  if (safety.kind === 'blocked') return 5;
  if (safety.kind === 'review' && safety.badge === 'Missing') return 6;
  if (safety.kind === 'review' && safety.badge === 'Workspace') return 7;
  if (safety.kind === 'ready' && safety.ageBucket === 'stale') return 8;
  if (safety.kind === 'ready') return 9;
  if (safety.kind === 'review') return 10;
  if (safety.kind === 'protected') return 11;
  return 12;
}

function worktreeActivitySortValue(worktree: ProjectWorktree): number {
  if (!worktree.lastActivity) return Number.POSITIVE_INFINITY;
  const value = new Date(worktree.lastActivity).getTime();
  return Number.isNaN(value) ? Number.POSITIVE_INFINITY : value;
}

function formatCleanupBriefReport(details: {
  headline: string;
  taskIDs: string[];
  blocked: Array<{ worktree: ProjectWorktree; safety: WorktreeSafetySummary }>;
  ready: Array<{ worktree: ProjectWorktree; safety: WorktreeSafetySummary }>;
  review: Array<{ worktree: ProjectWorktree; safety: WorktreeSafetySummary }>;
  protectedEntries: Array<{ worktree: ProjectWorktree; safety: WorktreeSafetySummary }>;
}): string {
  const lines = [
    'Worktree cleanup brief',
    details.headline,
    details.taskIDs.length > 0 ? `Tasks: ${details.taskIDs.join(', ')}` : '',
    `Blocked: ${details.blocked.length}`,
    `Cleanup candidates: ${details.ready.length}`,
    `Review: ${details.review.length}`,
    ''
  ].filter(Boolean);

  appendBriefSection(lines, 'Needs attention:', details.blocked);
  appendBriefSection(lines, 'Cleanup candidates:', details.ready);
  appendBriefSection(lines, 'Review:', details.review);
  appendBriefSection(lines, 'Protected main checkouts:', details.protectedEntries);

  return lines.join('\n');
}

function formatDecisionQueueGroupSummary(entries: WorktreeDecisionQueueEntry[]): string {
  const staleCount = entries.filter((entry) => entry.safety.ageBucket === 'stale').length;
  const activeCount = entries.reduce((total, entry) => total + entry.safety.activeSessionCount, 0);
  const savedWorkspaceCount = entries.reduce((total, entry) => total + entry.safety.savedWorkspaceCount, 0);
  const taskIDs = uniqueTaskIDs(entries.map((entry) => worktreeTaskID(entry.worktree)));
  const parts = [`${entries.length} ${entries.length === 1 ? 'worktree' : 'worktrees'}`];

  if (staleCount > 0) {
    parts.push(`${staleCount} stale`);
  }
  if (activeCount > 0) {
    parts.push(`${activeCount} active ${activeCount === 1 ? 'session' : 'sessions'}`);
  }
  if (savedWorkspaceCount > 0) {
    parts.push(`${savedWorkspaceCount} saved ${savedWorkspaceCount === 1 ? 'workspace' : 'workspaces'}`);
  }
  if (taskIDs.length > 0) {
    parts.push(`${taskIDs.length} ${taskIDs.length === 1 ? 'task' : 'tasks'}`);
  }

  return parts.join(' · ');
}

function formatWorktreeTaskGroup(taskID: string | null, entries: WorktreeDecisionQueueEntry[]): WorktreeTaskGroup {
  const blockedCount = entries.filter((entry) => entry.safety.kind === 'blocked').length;
  const reviewCount = entries.filter((entry) => entry.safety.kind === 'review').length;
  const protectedCount = entries.filter((entry) => entry.safety.kind === 'protected').length;
  const cleanupCandidateCount = entries.filter((entry) => entry.safety.kind === 'ready').length;
  const staleCount = entries.filter((entry) => entry.safety.ageBucket === 'stale').length;
  const activeSessionCount = entries.reduce((total, entry) => total + entry.safety.activeSessionCount, 0);
  const savedWorkspaceCount = entries.reduce((total, entry) => total + entry.safety.savedWorkspaceCount, 0);
  const needsBackupCount = entries.filter((entry) => entry.primaryAction.kind === 'backup').length;
  const primaryEntry = entries[0];
  const label = taskID ?? 'No task ID';
  const summary = formatTaskGroupSummary({
    worktreeCount: entries.length,
    blockedCount,
    reviewCount,
    protectedCount,
    cleanupCandidateCount,
    staleCount,
    activeSessionCount,
    savedWorkspaceCount,
    needsBackupCount
  });

  return {
    taskID,
    label,
    summary,
    worktreeCount: entries.length,
    cleanupCandidateCount,
    blockedCount,
    reviewCount,
    protectedCount,
    staleCount,
    activeSessionCount,
    savedWorkspaceCount,
    needsBackupCount,
    requiresManualSignoff: blockedCount > 0 || reviewCount > 0 || protectedCount > 0,
    primaryAction: primaryEntry.primaryAction,
    entries
  };
}

function formatTaskGroupSummary(details: {
  worktreeCount: number;
  blockedCount: number;
  reviewCount: number;
  protectedCount: number;
  cleanupCandidateCount: number;
  staleCount: number;
  activeSessionCount: number;
  savedWorkspaceCount: number;
  needsBackupCount: number;
}): string {
  const parts = [`${details.worktreeCount} ${details.worktreeCount === 1 ? 'worktree' : 'worktrees'}`];

  if (details.blockedCount > 0) parts.push(`${details.blockedCount} blocked`);
  if (details.needsBackupCount > 0) parts.push(`${details.needsBackupCount} need backup`);
  if (details.cleanupCandidateCount > 0) parts.push(`${details.cleanupCandidateCount} cleanup ready`);
  if (details.reviewCount > 0) parts.push(`${details.reviewCount} review`);
  if (details.protectedCount > 0) parts.push(`${details.protectedCount} protected`);
  if (details.staleCount > 0) parts.push(`${details.staleCount} stale`);
  if (details.activeSessionCount > 0) {
    parts.push(`${details.activeSessionCount} active ${details.activeSessionCount === 1 ? 'session' : 'sessions'}`);
  }
  if (details.savedWorkspaceCount > 0) {
    parts.push(`${details.savedWorkspaceCount} saved ${details.savedWorkspaceCount === 1 ? 'workspace' : 'workspaces'}`);
  }

  return parts.join(' · ');
}

function worktreeTaskGroupPriority(group: WorktreeTaskGroup): number {
  return group.entries.reduce(
    (bestPriority, entry) => Math.min(bestPriority, worktreeCleanupPriority(entry.safety)),
    Number.POSITIVE_INFINITY
  );
}

function appendBriefSection(
  lines: string[],
  title: string,
  entries: Array<{ worktree: ProjectWorktree; safety: WorktreeSafetySummary }>
): void {
  if (entries.length === 0) return;

  lines.push(title);
  for (const { worktree, safety } of entries) {
    const taskID = worktreeTaskID(worktree);
    const task = taskID ? ` · ${taskID}` : '';
    lines.push(`- ${worktree.branch}${task}: ${safety.reason} · ${safety.recommendation}`);
    lines.push(`  ${worktree.path}`);
  }
  lines.push('');
}

function uniqueTaskIDs(values: Array<string | null | undefined>): string[] {
  const seen = new Set<string>();
  const taskIDs: string[] = [];

  for (const value of values) {
    const taskID = normalizeTaskID(value);
    if (!taskID || seen.has(taskID)) continue;

    seen.add(taskID);
    taskIDs.push(taskID);
  }

  return taskIDs;
}

function normalizeTaskID(value: string | null | undefined): string | null {
  const taskID = value?.trim();
  if (!taskID) return null;
  return /^tsk-\d+$/i.test(taskID) ? taskID.toUpperCase() : taskID;
}

function worktreeTaskID(worktree: ProjectWorktree): string | null {
  return (
    normalizeTaskID(worktree.taskID) ??
    extractGitTaskIDs([worktree.branch, worktree.path].filter(Boolean).join(' '))[0] ??
    null
  );
}

function countOwnedPaths(
  worktreePath: string,
  ownedPaths: Array<string | null | undefined> | undefined
): number {
  if (!ownedPaths || ownedPaths.length === 0) return 0;

  return ownedPaths.filter((path) => {
    if (!path) return false;
    const sessionPath = normalizePath(path);
    return sessionPath === worktreePath || sessionPath.startsWith(`${worktreePath}/`);
  }).length;
}

function worktreeIsPrunable(worktree: ProjectWorktree): boolean {
  return Boolean(worktree.isPrunable) || worktreeEligibilityHasToken(worktree, 'prunable') || worktreeEligibilityHasToken(worktree, 'missing');
}

function worktreeIsLocked(worktree: ProjectWorktree): boolean {
  return Boolean(worktree.isLocked) || worktreeEligibilityHasToken(worktree, 'locked');
}

function worktreeIsDirty(worktree: ProjectWorktree): boolean {
  return Boolean(worktree.isDirty) || worktreeEligibilityHasToken(worktree, 'dirty') || worktreeHasUntrackedFiles(worktree);
}

function worktreeHasUntrackedFiles(worktree: ProjectWorktree): boolean {
  return worktreeEligibilityHasToken(worktree, 'untracked');
}

function worktreeHasUnmergedCommits(worktree: ProjectWorktree): boolean {
  return Boolean(worktree.hasUnmergedCommits) || worktreeEligibilityHasToken(worktree, 'unmerged');
}

function worktreeEligibilityHasToken(worktree: ProjectWorktree, token: string): boolean {
  const escapedToken = token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`(^|[^a-z0-9])${escapedToken}($|[^a-z0-9])`, 'i').test(worktree.deleteEligibility);
}

function worktreePrunableAuditCommand(primaryPath: string | null | undefined): string {
  if (!primaryPath) {
    return '# Open the main checkout first, then run git worktree list --porcelain and git worktree prune --dry-run.';
  }

  const gitInPrimary = ['git', '-C', shellQuote(primaryPath)].join(' ');
  return [
    `${gitInPrimary} worktree list --porcelain`,
    `${gitInPrimary} worktree prune --dry-run --verbose`
  ].join(' && ');
}

function worktreeActivity(
  lastActivity: string | null,
  nowMs: number,
  staleAfterDays: number
): { label: string; ageBucket: WorktreeAgeBucket } {
  if (!lastActivity) return { label: 'activity unknown', ageBucket: 'unknown' };

  const epochMs = new Date(lastActivity).getTime();
  if (Number.isNaN(epochMs)) return { label: lastActivity, ageBucket: 'unknown' };

  const elapsedMs = Math.max(0, nowMs - epochMs);
  const dayMs = 24 * 60 * 60 * 1000;
  const ageLabel = formatRelativeAge(elapsedMs);
  const ageBucket = elapsedMs >= staleAfterDays * dayMs ? 'stale' : 'active';
  return { label: ageLabel === 'just now' ? 'active just now' : `active ${ageLabel} ago`, ageBucket };
}

function formatRelativeAge(elapsedMs: number): string {
  const minuteMs = 60 * 1000;
  const hourMs = 60 * minuteMs;
  const dayMs = 24 * hourMs;

  if (elapsedMs < minuteMs) return 'just now';
  if (elapsedMs < hourMs) return `${Math.floor(elapsedMs / minuteMs)}m`;
  if (elapsedMs < dayMs) return `${Math.floor(elapsedMs / hourMs)}h`;
  return `${Math.floor(elapsedMs / dayMs)}d`;
}

function backupTimestamp(nowMs: number): string {
  return new Date(nowMs).toISOString().replace(/[:.]/g, '-');
}

function worktreeArchiveDirectory(worktree: ProjectWorktree, nowMs: number): string {
  const repoSegment = safePathSegment(worktree.repo || 'repo');
  const branchSegment = safePathSegment(worktree.branch || 'worktree');
  const archiveName = safePathSegment(`${repoSegment}-${branchSegment}-${backupTimestamp(nowMs)}`);
  return `${worktreeArchiveRoot(worktree.path)}/${repoSegment}/${archiveName}`;
}

function worktreeArchiveRoot(worktreePath: string): string {
  const normalizedPath = normalizePath(worktreePath);
  const marker = '/worktrees/';
  const markerIndex = normalizedPath.indexOf(marker);
  if (markerIndex >= 0) return `${normalizedPath.slice(0, markerIndex)}/worktree-archives`;

  return `${parentPath(normalizedPath)}/worktree-archives`;
}

function parentPath(path: string): string {
  const trimmed = path.replace(/\/+$/, '');
  const lastSlash = trimmed.lastIndexOf('/');
  if (lastSlash <= 0) return '.';
  return trimmed.slice(0, lastSlash);
}

function safePathSegment(value: string): string {
  return value.trim().replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || 'worktree';
}

function normalizeNow(now: number | Date | undefined): number {
  if (now instanceof Date) return now.getTime();
  return typeof now === 'number' ? now : Date.now();
}

function normalizePath(path: string): string {
  return path.replace(/\/+$/, '');
}

function shellQuote(value: string): string {
  return `'${value.replace(/'/g, "'\\''")}'`;
}
