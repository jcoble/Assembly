export type WorktreeCleanupLane =
  | 'keep'
  | 'safe-remove'
  | 'backup-first'
  | 'review-first'
  | 'blocked-active-session'
  | 'blocked-protected'
  | 'blocked-locked'
  | 'review-prunable'
  | 'review-saved-workspace'
  | 'review-confirmation';

export type WorktreeCleanupCommandPlanKind =
  | 'keep'
  | 'clean-remove'
  | 'dirty-backup'
  | 'branch-review'
  | 'active-session-warning'
  | 'protected-keep'
  | 'locked-warning'
  | 'prunable-review'
  | 'saved-workspace-review'
  | 'confirmation-review';

export type WorktreeCleanupTaskMetadata = {
  id?: string | null;
  url?: string | null;
  title?: string | null;
};

export type WorktreeCleanupTaskDisplay = {
  id: string;
  label: string;
  href?: string;
  title?: string;
};

export type WorktreeCleanupState = {
  repoName?: string | null;
  repoRootPath?: string | null;
  path: string;
  branch?: string | null;
  isMainRoot?: boolean | null;
  isProtected?: boolean | null;
  dirtyCount?: number | null;
  stagedCount?: number | null;
  untrackedCount?: number | null;
  aheadCount?: number | null;
  behindCount?: number | null;
  hasUpstream?: boolean | null;
  lastActivityAgeDays?: number | null;
  activeSessionCount?: number | null;
  savedWorkspaceCount?: number | null;
  isLocked?: boolean | null;
  lockedReason?: string | null;
  isPrunable?: boolean | null;
  prunableReason?: string | null;
  deleteEligibility?: string | null;
  task?: WorktreeCleanupTaskMetadata | null;
};

export type WorktreeCleanupPlanOptions = {
  staleAfterDays?: number;
  backupDirectory?: string | null;
  remoteName?: string | null;
};

export type WorktreeCleanupCommandPlan = {
  kind: WorktreeCleanupCommandPlanKind;
  steps: string[];
};

export type WorktreeCleanupFacts = {
  path: string;
  repoRootPath: string | null;
  branch: string | null;
  dirtyCount: number;
  stagedCount: number;
  untrackedCount: number;
  localChangeCount: number;
  aheadCount: number;
  behindCount: number;
  hasUpstream: boolean;
  lastActivityAgeDays: number | null;
  activeSessionCount: number;
  savedWorkspaceCount: number;
  staleAfterDays: number;
  isStale: boolean;
  isProtected: boolean;
  isLocked: boolean;
  lockedReason: string | null;
  isPrunable: boolean;
  prunableReason: string | null;
  deleteEligibility: string | null;
  requiresConfirmation: boolean;
};

export type WorktreeCleanupPlan = {
  lane: WorktreeCleanupLane;
  explanation: string;
  commandPlan: WorktreeCleanupCommandPlan;
  taskDisplay: WorktreeCleanupTaskDisplay | null;
  facts: WorktreeCleanupFacts;
};

const defaultStaleAfterDays = 14;
const defaultRemoteName = 'origin';

export function buildWorktreeCleanupPlan(
  state: WorktreeCleanupState,
  options: WorktreeCleanupPlanOptions = {}
): WorktreeCleanupPlan {
  const facts = normalizeFacts(state, options);
  const taskDisplay = buildTaskDisplay(state.task);

  if (facts.isProtected) {
    return {
      lane: 'blocked-protected',
      explanation: withTask('Protected main checkout; keep as the repository anchor.', taskDisplay),
      commandPlan: {
        kind: 'protected-keep',
        steps: [
          `Keep ${shellQuote(facts.path)} as the repository anchor.`,
          `git -C ${shellQuote(facts.repoRootPath ?? facts.path)} worktree list`,
          'Remove only sibling worktrees after their own cleanup plan says safe-remove.'
        ]
      },
      taskDisplay,
      facts
    };
  }

  if (facts.activeSessionCount > 0) {
    const sessions = pluralize(facts.activeSessionCount, 'active session', 'active sessions');
    return {
      lane: 'blocked-active-session',
      explanation: withTask(
        `${sessions} still point here; do not remove${formatInactiveSuffix(facts.lastActivityAgeDays)}.`,
        taskDisplay
      ),
      commandPlan: {
        kind: 'active-session-warning',
        steps: [
          `Do not remove: ${sessions} still point at this worktree.`,
          `git -C ${shellQuote(facts.path)} status --short --branch`,
          'Close, move, or resume those sessions before cleanup, then recompute the plan.'
        ]
      },
      taskDisplay,
      facts
    };
  }

  if (facts.isLocked) {
    return {
      lane: 'blocked-locked',
      explanation: withTask(
        `Locked worktree${facts.lockedReason ? `: ${facts.lockedReason}` : ''}; audit the lock before cleanup.`,
        taskDisplay
      ),
      commandPlan: {
        kind: 'locked-warning',
        steps: lockedReviewSteps(facts)
      },
      taskDisplay,
      facts
    };
  }

  if (facts.localChangeCount > 0) {
    return {
      lane: 'backup-first',
      explanation: withTask(
        `${pluralize(facts.localChangeCount, 'local change', 'local changes')} (${facts.dirtyCount} dirty, ${facts.stagedCount} staged, ${facts.untrackedCount} untracked); backup before removal.`,
        taskDisplay
      ),
      commandPlan: {
        kind: 'dirty-backup',
        steps: dirtyBackupSteps(facts, options)
      },
      taskDisplay,
      facts
    };
  }

  if (facts.aheadCount > 0 || facts.behindCount > 0) {
    return {
      lane: 'review-first',
      explanation: withTask(`${branchDivergenceLabel(facts)}; review, push, or preserve before removal.`, taskDisplay),
      commandPlan: {
        kind: 'branch-review',
        steps: branchReviewSteps(facts, options)
      },
      taskDisplay,
      facts
    };
  }

  if (facts.isPrunable) {
    return {
      lane: 'review-prunable',
      explanation: withTask(
        `Missing worktree path${facts.prunableReason ? `: ${facts.prunableReason}` : ''}; prune metadata only after confirmation.`,
        taskDisplay
      ),
      commandPlan: {
        kind: 'prunable-review',
        steps: prunableReviewSteps(facts)
      },
      taskDisplay,
      facts
    };
  }

  if (facts.savedWorkspaceCount > 0) {
    const snapshots = pluralize(facts.savedWorkspaceCount, 'saved workspace', 'saved workspaces');
    return {
      lane: 'review-saved-workspace',
      explanation: withTask(`${snapshots} still point here; restore or move them before cleanup.`, taskDisplay),
      commandPlan: {
        kind: 'saved-workspace-review',
        steps: savedWorkspaceReviewSteps(facts)
      },
      taskDisplay,
      facts
    };
  }

  if (facts.requiresConfirmation) {
    return {
      lane: 'review-confirmation',
      explanation: withTask(
        `Cleanup eligibility is ${facts.deleteEligibility ?? 'unknown'}; confirm ownership before removal.`,
        taskDisplay
      ),
      commandPlan: {
        kind: 'confirmation-review',
        steps: confirmationReviewSteps(facts)
      },
      taskDisplay,
      facts
    };
  }

  if (facts.isStale) {
    return {
      lane: 'safe-remove',
      explanation: withTask(
        `Clean and merged; ${facts.lastActivityAgeDays}d inactive; safe to remove after owner check.`,
        taskDisplay
      ),
      commandPlan: {
        kind: 'clean-remove',
        steps: [
          `git -C ${shellQuote(facts.repoRootPath ?? facts.path)} worktree remove ${shellQuote(facts.path)}`,
          `git -C ${shellQuote(facts.repoRootPath ?? facts.path)} worktree prune`
        ]
      },
      taskDisplay,
      facts
    };
  }

  return {
    lane: 'keep',
    explanation: withTask(
      `Clean but recently active${formatInactiveSuffix(facts.lastActivityAgeDays)}; keep until the task is done or stale.`,
      taskDisplay
    ),
    commandPlan: {
      kind: 'keep',
      steps: [
        `Keep ${shellQuote(facts.path)} for now.`,
        `Recompute cleanup after it reaches ${facts.staleAfterDays}d inactive or the task is closed.`
      ]
    },
    taskDisplay,
    facts
  };
}

function normalizeFacts(
  state: WorktreeCleanupState,
  options: WorktreeCleanupPlanOptions
): WorktreeCleanupFacts {
  const path = String(state.path ?? '').trim();
  const repoRootPath = normalizeOptionalString(state.repoRootPath);
  const branch = normalizeOptionalString(state.branch);
  const staleAfterDays = Math.max(0, normalizeCount(options.staleAfterDays ?? defaultStaleAfterDays));
  const lastActivityAgeDays = normalizeOptionalCount(state.lastActivityAgeDays);
  const dirtyCount = normalizeCount(state.dirtyCount);
  const stagedCount = normalizeCount(state.stagedCount);
  const untrackedCount = normalizeCount(state.untrackedCount);
  const activeSessionCount = normalizeCount(state.activeSessionCount);
  const savedWorkspaceCount = normalizeCount(state.savedWorkspaceCount);
  const isMainRoot = Boolean(state.isMainRoot || pathsEqual(path, repoRootPath));
  const isProtected = Boolean(state.isProtected || isMainRoot);
  const deleteEligibility = normalizeOptionalString(state.deleteEligibility);
  const deleteEligibilityText = (deleteEligibility ?? '').toLowerCase();

  return {
    path,
    repoRootPath,
    branch,
    dirtyCount,
    stagedCount,
    untrackedCount,
    localChangeCount: dirtyCount + stagedCount + untrackedCount,
    aheadCount: normalizeCount(state.aheadCount),
    behindCount: normalizeCount(state.behindCount),
    hasUpstream: state.hasUpstream !== false,
    lastActivityAgeDays,
    activeSessionCount,
    savedWorkspaceCount,
    staleAfterDays,
    isStale: lastActivityAgeDays !== null && lastActivityAgeDays >= staleAfterDays,
    isProtected,
    isLocked: Boolean(state.isLocked),
    lockedReason: normalizeOptionalString(state.lockedReason),
    isPrunable: Boolean(state.isPrunable),
    prunableReason: normalizeOptionalString(state.prunableReason),
    deleteEligibility,
    requiresConfirmation:
      deleteEligibilityText.includes('confirmation') || deleteEligibilityText.includes('unknown')
  };
}

function buildTaskDisplay(task: WorktreeCleanupTaskMetadata | null | undefined): WorktreeCleanupTaskDisplay | null {
  const id = normalizeOptionalString(task?.id);
  if (!id) return null;

  const href = normalizeOptionalString(task?.url);
  const title = normalizeOptionalString(task?.title);

  return {
    id,
    label: id,
    ...(href ? { href } : {}),
    ...(title ? { title } : {})
  };
}

function dirtyBackupSteps(
  facts: WorktreeCleanupFacts,
  options: WorktreeCleanupPlanOptions
): string[] {
  const backupDirectory = backupDirectoryFor(facts, options);
  const backupSlug = pathSlug(facts.path);
  const branchName = facts.branch ?? 'detached';
  const backupBranch = `backup/worktree-cleanup/${refSlug(branchName)}`;

  return [
    `mkdir -p ${shellQuote(backupDirectory)}`,
    `git -C ${shellQuote(facts.path)} status --short --branch > ${shellQuote(`${backupDirectory}/${backupSlug}-status.txt`)}`,
    `git -C ${shellQuote(facts.path)} branch ${shellQuote(backupBranch)}`,
    `git -C ${shellQuote(facts.path)} diff --binary > ${shellQuote(`${backupDirectory}/${backupSlug}-unstaged.patch`)}`,
    `git -C ${shellQuote(facts.path)} diff --cached --binary > ${shellQuote(`${backupDirectory}/${backupSlug}-staged.patch`)}`,
    `git -C ${shellQuote(facts.path)} ls-files --others --exclude-standard > ${shellQuote(`${backupDirectory}/${backupSlug}-untracked.txt`)}`,
    `git -C ${shellQuote(facts.path)} stash push --include-untracked -m ${shellQuote(`worktree cleanup backup: ${branchName}`)}`
  ];
}

function branchReviewSteps(
  facts: WorktreeCleanupFacts,
  options: WorktreeCleanupPlanOptions
): string[] {
  const remoteName = normalizeOptionalString(options.remoteName) ?? defaultRemoteName;
  const branchName = facts.branch ?? '';
  const hasAhead = facts.aheadCount > 0;
  const hasBehind = facts.behindCount > 0;
  const steps = [`git -C ${shellQuote(facts.path)} status --short --branch`];

  if (!facts.hasUpstream && hasAhead) {
    steps.push(`git -C ${shellQuote(facts.path)} log --oneline --decorate --max-count=20 HEAD`);
    if (branchName) {
      steps.push(`git -C ${shellQuote(facts.path)} push -u ${shellToken(remoteName)} ${shellQuote(branchName)}`);
    } else {
      steps.push(`git -C ${shellQuote(facts.path)} branch ${shellQuote(`backup/worktree-cleanup/${pathSlug(facts.path)}`)}`);
    }
  } else if (hasAhead && hasBehind) {
    steps.push(`git -C ${shellQuote(facts.path)} fetch ${shellToken(remoteName)}`);
    steps.push(`git -C ${shellQuote(facts.path)} log --left-right --oneline --decorate 'HEAD...@{upstream}'`);
    steps.push('Resolve branch divergence before pushing or removing this worktree.');
  } else if (hasAhead) {
    steps.push(`git -C ${shellQuote(facts.path)} log --oneline --decorate '@{upstream}..HEAD'`);
    if (branchName) {
      steps.push(`git -C ${shellQuote(facts.path)} push -u ${shellToken(remoteName)} ${shellQuote(branchName)}`);
    } else {
      steps.push(`git -C ${shellQuote(facts.path)} branch ${shellQuote(`backup/worktree-cleanup/${pathSlug(facts.path)}`)}`);
    }
  } else if (hasBehind) {
    steps.push(`git -C ${shellQuote(facts.path)} fetch ${shellToken(remoteName)}`);
    steps.push(`git -C ${shellQuote(facts.path)} log --oneline --decorate 'HEAD..@{upstream}'`);
    steps.push('Review incoming commits; rebase or merge only after confirming branch ownership.');
  }

  steps.push('After the branch is pushed, merged, or archived, rerun the cleanup plan.');
  return steps;
}

function lockedReviewSteps(facts: WorktreeCleanupFacts): string[] {
  const root = facts.repoRootPath ?? facts.path;
  const steps = [
    `git -C ${shellQuote(root)} worktree list --porcelain`,
    `git -C ${shellQuote(facts.path)} status --short --branch`,
    'Do not unlock or remove this worktree until the lock owner and reason are understood.'
  ];

  if (facts.lockedReason) {
    steps.push(`Lock reason: ${facts.lockedReason}`);
  }

  steps.push('After the lock is intentionally cleared, rerun the cleanup plan.');
  return steps;
}

function prunableReviewSteps(facts: WorktreeCleanupFacts): string[] {
  const root = facts.repoRootPath ?? facts.path;
  const steps = [
    `git -C ${shellQuote(root)} worktree list --porcelain`,
    `git -C ${shellQuote(root)} worktree prune --dry-run`,
    'Confirm the missing path is intentionally gone and not a disconnected disk or moved folder.',
    `git -C ${shellQuote(root)} worktree prune`
  ];

  if (facts.prunableReason) {
    steps.splice(3, 0, `Prunable reason: ${facts.prunableReason}`);
  }

  return steps;
}

function savedWorkspaceReviewSteps(facts: WorktreeCleanupFacts): string[] {
  return [
    `Do not remove: ${pluralize(facts.savedWorkspaceCount, 'saved workspace', 'saved workspaces')} still point at this worktree.`,
    'Restore, move, or delete those saved workspace snapshots first.',
    `git -C ${shellQuote(facts.path)} status --short --branch`,
    'After saved workspaces are no longer attached, rerun the cleanup plan.'
  ];
}

function confirmationReviewSteps(facts: WorktreeCleanupFacts): string[] {
  const root = facts.repoRootPath ?? facts.path;
  return [
    `git -C ${shellQuote(facts.path)} status --short --branch`,
    `git -C ${shellQuote(root)} worktree list`,
    'Confirm no active agent, terminal, or saved workspace owns this worktree.',
    'After ownership is confirmed, rerun the cleanup plan.'
  ];
}

function backupDirectoryFor(
  facts: WorktreeCleanupFacts,
  options: WorktreeCleanupPlanOptions
): string {
  const configured = normalizeOptionalString(options.backupDirectory);
  if (configured) return configured;

  return `${facts.repoRootPath ?? facts.path}/.worktree-cleanup-backups`;
}

function branchDivergenceLabel(facts: WorktreeCleanupFacts): string {
  const parts = [];
  if (facts.aheadCount > 0) {
    parts.push(`${pluralize(facts.aheadCount, 'commit', 'commits')} ahead`);
  }
  if (facts.behindCount > 0) {
    parts.push(`${pluralize(facts.behindCount, 'commit', 'commits')} behind`);
  }

  return `Branch is ${parts.join(' and ')}`;
}

function withTask(explanation: string, taskDisplay: WorktreeCleanupTaskDisplay | null): string {
  return taskDisplay ? `${explanation} Task ${taskDisplay.id} linked.` : explanation;
}

function formatInactiveSuffix(ageDays: number | null): string {
  return ageDays === null ? '' : ` even though ${ageDays}d inactive`;
}

function pluralize(count: number, singular: string, plural: string): string {
  return `${count} ${count === 1 ? singular : plural}`;
}

function normalizeCount(value: number | null | undefined): number {
  const numeric = Number(value ?? 0);
  if (!Number.isFinite(numeric)) return 0;

  return Math.max(0, Math.trunc(numeric));
}

function normalizeOptionalCount(value: number | null | undefined): number | null {
  if (value === null || value === undefined) return null;

  const numeric = Number(value);
  if (!Number.isFinite(numeric)) return null;

  return Math.max(0, Math.trunc(numeric));
}

function normalizeOptionalString(value: string | null | undefined): string | null {
  const normalized = String(value ?? '').trim();
  return normalized.length > 0 ? normalized : null;
}

function pathsEqual(left: string, right: string | null): boolean {
  if (!right) return false;
  return trimTrailingSlash(left) === trimTrailingSlash(right);
}

function trimTrailingSlash(path: string): string {
  return path.replace(/\/+$/g, '');
}

function pathSlug(path: string): string {
  const parts = trimTrailingSlash(path).split('/').filter(Boolean);
  return refSlug(parts.at(-1) ?? 'worktree');
}

function refSlug(input: string): string {
  const normalized = input
    .replace(/^refs\/heads\//, '')
    .trim()
    .replace(/[^A-Za-z0-9._-]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');

  return normalized || 'worktree';
}

function shellQuote(value: string): string {
  return `'${value.replace(/'/g, "'\\''")}'`;
}

function shellToken(value: string): string {
  return /^[A-Za-z0-9._/-]+$/.test(value) ? value : shellQuote(value);
}
