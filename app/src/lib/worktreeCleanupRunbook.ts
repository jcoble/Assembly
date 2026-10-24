import { extractGitTaskIDs, normalizeGitTaskID } from './shell/git/gitTaskLinks.ts';
import type {
  WorktreeCleanupCommandPlanKind,
  WorktreeCleanupLane,
  WorktreeCleanupPlan,
  WorktreeCleanupTaskDisplay
} from './worktreeCleanupPlan.ts';
import {
  worktreePrimaryAction,
  type WorktreeDecisionQueueEntry,
  type WorktreeDecisionQueueGroup,
  type WorktreeDecisionQueueID,
  type WorktreePrimaryActionKind,
  type WorktreeSafetySummary
} from './worktreeSafety.ts';

export type WorktreeCleanupRunbookTone = 'safe' | 'backup' | 'review' | 'blocked' | 'protected' | 'keep';

export type WorktreeCleanupRunbookStateID =
  | 'clean-stale'
  | 'clean-recent'
  | 'dirty-local-changes'
  | 'branch-review'
  | 'active-session'
  | 'protected-checkout'
  | 'locked-worktree'
  | 'missing-metadata'
  | 'saved-workspace'
  | 'confirmation-review';

export type WorktreeCleanupRunbookSafetyInput = {
  path: string;
  repoName?: string | null;
  branch?: string | null;
  taskID?: string | null;
  safety: WorktreeSafetySummary;
};

export type WorktreeCleanupRunbookInput = {
  cleanupPlans?: readonly WorktreeCleanupPlan[];
  decisionQueue?: readonly WorktreeDecisionQueueGroup[];
  safetySummaries?: readonly WorktreeCleanupRunbookSafetyInput[];
};

export type WorktreeCleanupRunbookCounts = {
  totalWorktrees: number;
  safeRemovable: number;
  backupRequired: number;
  reviewRequired: number;
  blocked: number;
  protectedOrActive: number;
  protected: number;
  activeSessionBlocked: number;
  lockedBlocked: number;
  savedWorkspaceReview: number;
  commandBlocks: number;
};

export type WorktreeCleanupRunbookCommandBlock = {
  title: string;
  kind: WorktreeCleanupCommandPlanKind | WorktreePrimaryActionKind;
  steps: string[];
  copyText: string;
  stepCount: number;
};

export type WorktreeCleanupRunbookTaskRef = {
  id: string;
  label: string;
  href?: string;
  title?: string;
};

export type WorktreeCleanupRunbookItem = {
  id: string;
  repoName: string | null;
  path: string;
  branch: string;
  task: WorktreeCleanupRunbookTaskRef | null;
  lane: WorktreeCleanupLane;
  laneLabel: string;
  laneDetail: string;
  tone: WorktreeCleanupRunbookTone;
  stateID: WorktreeCleanupRunbookStateID;
  stateLabel: string;
  stateDetail: string;
  explanation: string;
  actionLabel: string;
  activeSessionCount: number;
  savedWorkspaceCount: number;
  localChangeCount: number;
  aheadCount: number;
  behindCount: number;
  isStale: boolean;
  decisionQueueID: WorktreeDecisionQueueID | null;
  safetyBadge: string | null;
  safetyReason: string | null;
  safetyRecommendation: string | null;
  source: {
    hasCleanupPlan: boolean;
    hasDecisionQueue: boolean;
    hasSafetySummary: boolean;
  };
  commandBlocks: WorktreeCleanupRunbookCommandBlock[];
  copyText: string;
};

export type WorktreeCleanupRunbookGroup = {
  id: string;
  label: string;
  detail: string;
  tone: WorktreeCleanupRunbookTone;
  count: number;
  summary: string;
  items: WorktreeCleanupRunbookItem[];
};

export type WorktreeCleanupRunbookModel = {
  headline: string;
  counts: WorktreeCleanupRunbookCounts;
  items: WorktreeCleanupRunbookItem[];
  lanes: WorktreeCleanupRunbookGroup[];
  tasks: WorktreeCleanupRunbookGroup[];
  states: WorktreeCleanupRunbookGroup[];
  copyText: string;
};

type IndexedDecisionEntry = {
  entry: WorktreeDecisionQueueEntry;
  queueID: WorktreeDecisionQueueID;
  queueLabel: string;
};

const laneOrder: Record<WorktreeCleanupLane, number> = {
  'blocked-active-session': 0,
  'blocked-protected': 1,
  'blocked-locked': 2,
  'backup-first': 3,
  'review-first': 4,
  'review-saved-workspace': 5,
  'review-prunable': 6,
  'review-confirmation': 7,
  'safe-remove': 8,
  keep: 9
};

export function buildWorktreeCleanupRunbook(
  input: WorktreeCleanupRunbookInput
): WorktreeCleanupRunbookModel {
  const queueEntries = flattenDecisionQueue(input.decisionQueue ?? []);
  const queueByPath = keyedByPath(queueEntries, ({ entry }) => entry.worktree.path);
  const safetyInputs = input.safetySummaries ?? [];
  const safetyByPath = keyedByPath(safetyInputs, (summary) => summary.path);
  const plannedPaths = new Set<string>();
  const items: WorktreeCleanupRunbookItem[] = [];

  for (const [index, plan] of (input.cleanupPlans ?? []).entries()) {
    const pathKey = normalizePath(plan.facts.path);
    plannedPaths.add(pathKey);
    items.push(
      runbookItemFromCleanupPlan({
        plan,
        decisionEntry: queueByPath.get(pathKey) ?? null,
        safetyInput: safetyByPath.get(pathKey) ?? null,
        index
      })
    );
  }

  let fallbackIndex = items.length;
  for (const decisionEntry of queueEntries) {
    const pathKey = normalizePath(decisionEntry.entry.worktree.path);
    if (plannedPaths.has(pathKey)) continue;

    items.push(
      runbookItemFromDecisionEntry({
        decisionEntry,
        safetyInput: safetyByPath.get(pathKey) ?? null,
        index: fallbackIndex
      })
    );
    fallbackIndex += 1;
  }

  const coveredPaths = new Set(items.map((item) => normalizePath(item.path)));
  for (const safetyInput of safetyInputs) {
    const pathKey = normalizePath(safetyInput.path);
    if (coveredPaths.has(pathKey)) continue;

    items.push(runbookItemFromSafetyInput(safetyInput, fallbackIndex));
    fallbackIndex += 1;
  }

  items.sort(compareRunbookItems);

  const counts = runbookCounts(items);
  const lanes = groupRunbookItems(items, 'lane');
  const tasks = groupRunbookItems(items, 'task');
  const states = groupRunbookItems(items, 'state');
  const headline = runbookHeadline(counts);

  return {
    headline,
    counts,
    items,
    lanes,
    tasks,
    states,
    copyText: formatRunbookCopyText(headline, lanes)
  };
}

export function worktreeCleanupRunbookLaneDescriptor(lane: WorktreeCleanupLane): {
  label: string;
  detail: string;
  tone: WorktreeCleanupRunbookTone;
} {
  switch (lane) {
    case 'safe-remove':
      return {
        label: 'Safe remove',
        detail: 'Clean, merged, and old enough to remove after final ownership confirmation.',
        tone: 'safe'
      };
    case 'backup-first':
      return {
        label: 'Backup first',
        detail: 'Local files or uncommitted work must be archived, committed, or stashed before removal.',
        tone: 'backup'
      };
    case 'review-first':
      return {
        label: 'Review first',
        detail: 'Branch state needs human review before this worktree can be removed.',
        tone: 'review'
      };
    case 'blocked-active-session':
      return {
        label: 'Blocked: active session',
        detail: 'An active agent, terminal, or session still points at this path.',
        tone: 'blocked'
      };
    case 'blocked-protected':
      return {
        label: 'Protected checkout',
        detail: 'This is the protected repository anchor and should not be removed.',
        tone: 'protected'
      };
    case 'blocked-locked':
      return {
        label: 'Blocked: locked',
        detail: 'Git has marked this worktree locked; understand the lock before cleanup.',
        tone: 'blocked'
      };
    case 'review-saved-workspace':
      return {
        label: 'Saved workspace review',
        detail: 'Saved workspace snapshots still reference this path.',
        tone: 'review'
      };
    case 'review-prunable':
      return {
        label: 'Review missing metadata',
        detail: 'The path is missing; prune Git metadata only after confirming it is intentionally gone.',
        tone: 'review'
      };
    case 'review-confirmation':
      return {
        label: 'Review required',
        detail: 'Cleanup eligibility requires explicit ownership confirmation.',
        tone: 'review'
      };
    case 'keep':
      return {
        label: 'Keep for now',
        detail: 'Clean but recently active, or otherwise not ready for cleanup.',
        tone: 'keep'
      };
  }
}

function runbookItemFromCleanupPlan(details: {
  plan: WorktreeCleanupPlan;
  decisionEntry: IndexedDecisionEntry | null;
  safetyInput: WorktreeCleanupRunbookSafetyInput | null;
  index: number;
}): WorktreeCleanupRunbookItem {
  const { plan, decisionEntry, safetyInput, index } = details;
  const descriptor = worktreeCleanupRunbookLaneDescriptor(plan.lane);
  const state = runbookStateFromPlan(plan);
  const branch = plan.facts.branch ?? decisionEntry?.entry.worktree.branch ?? safetyInput?.branch ?? pathBasename(plan.facts.path);
  const task = runbookTaskRef(plan.taskDisplay, decisionEntry?.entry.worktree.taskID ?? safetyInput?.taskID, [
    branch,
    plan.facts.path
  ]);
  const commandBlocks = commandBlocksFromCleanupPlan(plan);
  const item = finishRunbookItem({
    id: `worktree:${plan.facts.path}`,
    repoName: decisionEntry?.entry.worktree.repo ?? safetyInput?.repoName ?? null,
    path: plan.facts.path,
    branch,
    task,
    lane: plan.lane,
    laneLabel: descriptor.label,
    laneDetail: descriptor.detail,
    tone: descriptor.tone,
    stateID: state.id,
    stateLabel: state.label,
    stateDetail: state.detail,
    explanation: plan.explanation,
    actionLabel: descriptor.label,
    activeSessionCount: plan.facts.activeSessionCount,
    savedWorkspaceCount: plan.facts.savedWorkspaceCount,
    localChangeCount: plan.facts.localChangeCount,
    aheadCount: plan.facts.aheadCount,
    behindCount: plan.facts.behindCount,
    isStale: plan.facts.isStale,
    decisionQueueID: decisionEntry?.queueID ?? null,
    safetyBadge: decisionEntry?.entry.safety.badge ?? safetyInput?.safety.badge ?? null,
    safetyReason: decisionEntry?.entry.safety.reason ?? safetyInput?.safety.reason ?? null,
    safetyRecommendation: decisionEntry?.entry.safety.recommendation ?? safetyInput?.safety.recommendation ?? null,
    source: {
      hasCleanupPlan: true,
      hasDecisionQueue: Boolean(decisionEntry),
      hasSafetySummary: Boolean(safetyInput)
    },
    commandBlocks,
    copyText: '',
    index
  });

  return item;
}

function runbookItemFromDecisionEntry(details: {
  decisionEntry: IndexedDecisionEntry;
  safetyInput: WorktreeCleanupRunbookSafetyInput | null;
  index: number;
}): WorktreeCleanupRunbookItem {
  const { decisionEntry, safetyInput, index } = details;
  const { worktree, safety, primaryAction } = decisionEntry.entry;
  const lane = cleanupLaneFromSafety(safety);
  const descriptor = worktreeCleanupRunbookLaneDescriptor(lane);
  const state = runbookStateFromSafety(safety);
  const task = runbookTaskRef(null, worktree.taskID ?? safetyInput?.taskID, [worktree.branch, worktree.path]);
  const commandBlocks = [
    commandBlock(`${primaryAction.label} command`, primaryAction.kind, [primaryAction.command])
  ];

  return finishRunbookItem({
    id: `worktree:${worktree.path}`,
    repoName: worktree.repo || safetyInput?.repoName || null,
    path: worktree.path,
    branch: worktree.branch || safetyInput?.branch || pathBasename(worktree.path),
    task,
    lane,
    laneLabel: descriptor.label,
    laneDetail: descriptor.detail,
    tone: descriptor.tone,
    stateID: state.id,
    stateLabel: state.label,
    stateDetail: state.detail,
    explanation: safety.recommendation,
    actionLabel: primaryAction.label,
    activeSessionCount: safety.activeSessionCount,
    savedWorkspaceCount: safety.savedWorkspaceCount,
    localChangeCount: primaryAction.kind === 'backup' ? 1 : 0,
    aheadCount: 0,
    behindCount: 0,
    isStale: safety.ageBucket === 'stale',
    decisionQueueID: decisionEntry.queueID,
    safetyBadge: safety.badge,
    safetyReason: safety.reason,
    safetyRecommendation: safety.recommendation,
    source: {
      hasCleanupPlan: false,
      hasDecisionQueue: true,
      hasSafetySummary: Boolean(safetyInput)
    },
    commandBlocks,
    copyText: '',
    index
  });
}

function runbookItemFromSafetyInput(
  safetyInput: WorktreeCleanupRunbookSafetyInput,
  index: number
): WorktreeCleanupRunbookItem {
  const safety = safetyInput.safety;
  const lane = cleanupLaneFromSafety(safety);
  const descriptor = worktreeCleanupRunbookLaneDescriptor(lane);
  const state = runbookStateFromSafety(safety);
  const action = worktreePrimaryAction(safety);
  const branch = safetyInput.branch ?? pathBasename(safetyInput.path);
  const task = runbookTaskRef(null, safetyInput.taskID, [branch, safetyInput.path]);

  return finishRunbookItem({
    id: `worktree:${safetyInput.path}`,
    repoName: safetyInput.repoName ?? null,
    path: safetyInput.path,
    branch,
    task,
    lane,
    laneLabel: descriptor.label,
    laneDetail: descriptor.detail,
    tone: descriptor.tone,
    stateID: state.id,
    stateLabel: state.label,
    stateDetail: state.detail,
    explanation: safety.recommendation,
    actionLabel: action.label,
    activeSessionCount: safety.activeSessionCount,
    savedWorkspaceCount: safety.savedWorkspaceCount,
    localChangeCount: action.kind === 'backup' ? 1 : 0,
    aheadCount: 0,
    behindCount: 0,
    isStale: safety.ageBucket === 'stale',
    decisionQueueID: null,
    safetyBadge: safety.badge,
    safetyReason: safety.reason,
    safetyRecommendation: safety.recommendation,
    source: {
      hasCleanupPlan: false,
      hasDecisionQueue: false,
      hasSafetySummary: true
    },
    commandBlocks: [commandBlock(`${action.label} command`, action.kind, [action.command])],
    copyText: '',
    index
  });
}

function finishRunbookItem(
  item: WorktreeCleanupRunbookItem & { index: number }
): WorktreeCleanupRunbookItem {
  const { index: _index, ...runbookItem } = item;
  return {
    ...runbookItem,
    copyText: formatItemCopyText(runbookItem)
  };
}

function commandBlocksFromCleanupPlan(plan: WorktreeCleanupPlan): WorktreeCleanupRunbookCommandBlock[] {
  if (plan.commandPlan.steps.length === 0) return [];

  const descriptor = worktreeCleanupRunbookLaneDescriptor(plan.lane);
  return [
    commandBlock(`${descriptor.label} steps`, plan.commandPlan.kind, plan.commandPlan.steps)
  ];
}

function commandBlock(
  title: string,
  kind: WorktreeCleanupCommandPlanKind | WorktreePrimaryActionKind,
  steps: readonly string[]
): WorktreeCleanupRunbookCommandBlock {
  const normalizedSteps = steps.map((step) => step.trim()).filter(Boolean);

  return {
    title,
    kind,
    steps: normalizedSteps,
    copyText: normalizedSteps.join('\n'),
    stepCount: normalizedSteps.length
  };
}

function runbookTaskRef(
  taskDisplay: WorktreeCleanupTaskDisplay | null,
  fallbackTaskID: string | null | undefined,
  searchText: readonly string[]
): WorktreeCleanupRunbookTaskRef | null {
  const taskID =
    normalizeGitTaskID(taskDisplay?.id) ??
    normalizeGitTaskID(fallbackTaskID) ??
    extractGitTaskIDs(searchText.filter(Boolean).join(' '))[0] ??
    null;

  if (!taskID) return null;

  return {
    id: taskID,
    label: taskID,
    ...(taskDisplay?.href ? { href: taskDisplay.href } : {}),
    ...(taskDisplay?.title ? { title: taskDisplay.title } : {})
  };
}

function runbookStateFromPlan(plan: WorktreeCleanupPlan): {
  id: WorktreeCleanupRunbookStateID;
  label: string;
  detail: string;
} {
  const facts = plan.facts;

  if (facts.activeSessionCount > 0) {
    return {
      id: 'active-session',
      label: 'Active session',
      detail: `${facts.activeSessionCount} active ${facts.activeSessionCount === 1 ? 'session' : 'sessions'} still point here.`
    };
  }
  if (facts.isProtected) {
    return {
      id: 'protected-checkout',
      label: 'Protected checkout',
      detail: 'Protected repository anchor.'
    };
  }
  if (facts.isLocked) {
    return {
      id: 'locked-worktree',
      label: 'Locked worktree',
      detail: facts.lockedReason ? `Locked: ${facts.lockedReason}` : 'Git lock must be audited before cleanup.'
    };
  }
  if (facts.localChangeCount > 0) {
    return {
      id: 'dirty-local-changes',
      label: 'Dirty local changes',
      detail: `${facts.localChangeCount} local ${facts.localChangeCount === 1 ? 'change' : 'changes'} need backup.`
    };
  }
  if (facts.aheadCount > 0 || facts.behindCount > 0) {
    return {
      id: 'branch-review',
      label: 'Branch review',
      detail: branchReviewDetail(facts.aheadCount, facts.behindCount)
    };
  }
  if (facts.isPrunable) {
    return {
      id: 'missing-metadata',
      label: 'Missing metadata',
      detail: facts.prunableReason ?? 'Missing path needs metadata prune review.'
    };
  }
  if (facts.savedWorkspaceCount > 0) {
    return {
      id: 'saved-workspace',
      label: 'Saved workspace',
      detail: `${facts.savedWorkspaceCount} saved ${facts.savedWorkspaceCount === 1 ? 'workspace' : 'workspaces'} still point here.`
    };
  }
  if (plan.lane === 'review-confirmation') {
    return {
      id: 'confirmation-review',
      label: 'Confirmation review',
      detail: 'Ownership confirmation is required before cleanup.'
    };
  }
  if (facts.isStale) {
    return {
      id: 'clean-stale',
      label: 'Clean stale',
      detail: facts.lastActivityAgeDays === null ? 'Clean stale worktree.' : `${facts.lastActivityAgeDays}d inactive.`
    };
  }

  return {
    id: 'clean-recent',
    label: 'Clean recent',
    detail: 'Clean but not stale enough for automatic cleanup.'
  };
}

function runbookStateFromSafety(summary: WorktreeSafetySummary): {
  id: WorktreeCleanupRunbookStateID;
  label: string;
  detail: string;
} {
  if (summary.activeSessionCount > 0) {
    return {
      id: 'active-session',
      label: 'Active session',
      detail: summary.reason
    };
  }
  if (summary.kind === 'protected') {
    return {
      id: 'protected-checkout',
      label: 'Protected checkout',
      detail: summary.reason
    };
  }
  if (summary.badge === 'Locked') {
    return {
      id: 'locked-worktree',
      label: 'Locked worktree',
      detail: summary.reason
    };
  }
  if (summary.badge === 'Dirty' || summary.badge === 'Untracked' || summary.badge === 'Unmerged') {
    return {
      id: summary.badge === 'Unmerged' ? 'branch-review' : 'dirty-local-changes',
      label: summary.badge === 'Unmerged' ? 'Branch review' : 'Dirty local changes',
      detail: summary.reason
    };
  }
  if (summary.badge === 'Missing') {
    return {
      id: 'missing-metadata',
      label: 'Missing metadata',
      detail: summary.reason
    };
  }
  if (summary.badge === 'Workspace' || summary.savedWorkspaceCount > 0) {
    return {
      id: 'saved-workspace',
      label: 'Saved workspace',
      detail: summary.reason
    };
  }
  if (summary.kind === 'review') {
    return {
      id: 'confirmation-review',
      label: 'Confirmation review',
      detail: summary.reason
    };
  }

  return summary.ageBucket === 'stale'
    ? { id: 'clean-stale', label: 'Clean stale', detail: summary.activityLabel }
    : { id: 'clean-recent', label: 'Clean recent', detail: summary.activityLabel };
}

function cleanupLaneFromSafety(summary: WorktreeSafetySummary): WorktreeCleanupLane {
  if (summary.kind === 'protected') return 'blocked-protected';
  if (summary.activeSessionCount > 0) return 'blocked-active-session';
  if (summary.badge === 'Locked') return 'blocked-locked';
  if (summary.badge === 'Dirty' || summary.badge === 'Untracked' || summary.badge === 'Unmerged') {
    return 'backup-first';
  }
  if (summary.badge === 'Missing') return 'review-prunable';
  if (summary.badge === 'Workspace' || summary.savedWorkspaceCount > 0) return 'review-saved-workspace';
  if (summary.kind === 'ready') return 'safe-remove';
  if (summary.kind === 'review') return 'review-confirmation';

  return 'review-first';
}

function flattenDecisionQueue(groups: readonly WorktreeDecisionQueueGroup[]): IndexedDecisionEntry[] {
  return groups.flatMap((group) =>
    group.entries.map((entry) => ({
      entry,
      queueID: group.id,
      queueLabel: group.label
    }))
  );
}

function keyedByPath<T>(items: readonly T[], pathForItem: (item: T) => string): Map<string, T> {
  const map = new Map<string, T>();

  for (const item of items) {
    const key = normalizePath(pathForItem(item));
    if (!key) continue;
    map.set(key, item);
  }

  return map;
}

function groupRunbookItems(
  items: WorktreeCleanupRunbookItem[],
  kind: 'lane' | 'task' | 'state'
): WorktreeCleanupRunbookGroup[] {
  const groups = new Map<string, WorktreeCleanupRunbookItem[]>();

  for (const item of items) {
    const key = groupKey(item, kind);
    groups.set(key, [...(groups.get(key) ?? []), item]);
  }

  return Array.from(groups.entries())
    .map(([key, groupItems]) => runbookGroup(key, groupItems, kind))
    .sort((left, right) => compareRunbookGroups(left, right, kind));
}

function groupKey(item: WorktreeCleanupRunbookItem, kind: 'lane' | 'task' | 'state'): string {
  if (kind === 'lane') return item.lane;
  if (kind === 'state') return item.stateID;
  return item.task?.id ?? '__no_task__';
}

function runbookGroup(
  key: string,
  items: WorktreeCleanupRunbookItem[],
  kind: 'lane' | 'task' | 'state'
): WorktreeCleanupRunbookGroup {
  const sortedItems = [...items].sort(compareRunbookItems);

  if (kind === 'lane') {
    const lane = key as WorktreeCleanupLane;
    const descriptor = worktreeCleanupRunbookLaneDescriptor(lane);
    return {
      id: lane,
      label: descriptor.label,
      detail: descriptor.detail,
      tone: descriptor.tone,
      count: sortedItems.length,
      summary: formatGroupSummary(sortedItems),
      items: sortedItems
    };
  }

  if (kind === 'state') {
    const first = sortedItems[0];
    return {
      id: key,
      label: first?.stateLabel ?? key,
      detail: first?.stateDetail ?? '',
      tone: first?.tone ?? 'review',
      count: sortedItems.length,
      summary: formatGroupSummary(sortedItems),
      items: sortedItems
    };
  }

  const firstTask = sortedItems.find((item) => item.task)?.task ?? null;
  return {
    id: key,
    label: firstTask?.label ?? 'No task ID',
    detail: firstTask?.title ?? 'Worktrees without a linked task ID.',
    tone: highestPriorityTone(sortedItems),
    count: sortedItems.length,
    summary: formatGroupSummary(sortedItems),
    items: sortedItems
  };
}

function runbookCounts(items: WorktreeCleanupRunbookItem[]): WorktreeCleanupRunbookCounts {
  return {
    totalWorktrees: items.length,
    safeRemovable: items.filter((item) => item.lane === 'safe-remove').length,
    backupRequired: items.filter((item) => item.lane === 'backup-first').length,
    reviewRequired: items.filter((item) => isReviewLane(item.lane)).length,
    blocked: items.filter((item) => isBlockedLane(item.lane)).length,
    protectedOrActive: items.filter(
      (item) => item.lane === 'blocked-protected' || item.lane === 'blocked-active-session'
    ).length,
    protected: items.filter((item) => item.lane === 'blocked-protected').length,
    activeSessionBlocked: items.filter((item) => item.lane === 'blocked-active-session').length,
    lockedBlocked: items.filter((item) => item.lane === 'blocked-locked').length,
    savedWorkspaceReview: items.filter((item) => item.lane === 'review-saved-workspace').length,
    commandBlocks: items.reduce((total, item) => total + item.commandBlocks.length, 0)
  };
}

function runbookHeadline(counts: WorktreeCleanupRunbookCounts): string {
  return [
    `${counts.totalWorktrees} ${counts.totalWorktrees === 1 ? 'worktree' : 'worktrees'}`,
    `${counts.safeRemovable} safe/removable`,
    `${counts.backupRequired} backup required`,
    `${counts.reviewRequired} review`,
    `${counts.blocked} blocked`,
    `${counts.protectedOrActive} protected/active`,
    `${counts.savedWorkspaceReview} saved workspace/review`
  ].join(' · ');
}

function formatRunbookCopyText(
  headline: string,
  laneGroups: WorktreeCleanupRunbookGroup[]
): string {
  const lines = ['Worktree cleanup runbook', headline, ''];

  for (const group of laneGroups) {
    lines.push(`${group.label} (${group.count}) - ${group.summary}`);
    for (const item of group.items) {
      lines.push(`- ${item.branch}${item.task ? ` · ${item.task.id}` : ''}: ${item.stateLabel}`);
      lines.push(`  ${item.path}`);
      lines.push(`  ${item.explanation}`);
      for (const block of item.commandBlocks) {
        lines.push(`  ${block.title}:`);
        for (const step of block.steps) {
          lines.push(`    ${step}`);
        }
      }
    }
    lines.push('');
  }

  return lines.join('\n').trimEnd();
}

function formatItemCopyText(item: Omit<WorktreeCleanupRunbookItem, 'copyText'>): string {
  const lines = [
    `${item.laneLabel}: ${item.branch}`,
    item.task ? `Task: ${item.task.id}` : '',
    `Path: ${item.path}`,
    `State: ${item.stateLabel} - ${item.stateDetail}`,
    `Decision: ${item.explanation}`
  ].filter(Boolean);

  for (const block of item.commandBlocks) {
    lines.push('');
    lines.push(block.title);
    lines.push(block.copyText);
  }

  return lines.join('\n');
}

function formatGroupSummary(items: WorktreeCleanupRunbookItem[]): string {
  const counts = runbookCounts(items);
  const parts = [`${items.length} ${items.length === 1 ? 'worktree' : 'worktrees'}`];

  if (counts.safeRemovable > 0) parts.push(`${counts.safeRemovable} safe/removable`);
  if (counts.backupRequired > 0) parts.push(`${counts.backupRequired} backup required`);
  if (counts.reviewRequired > 0) parts.push(`${counts.reviewRequired} review`);
  if (counts.blocked > 0) parts.push(`${counts.blocked} blocked`);
  if (counts.protectedOrActive > 0) parts.push(`${counts.protectedOrActive} protected/active`);
  if (counts.savedWorkspaceReview > 0) parts.push(`${counts.savedWorkspaceReview} saved workspace/review`);

  return parts.join(' · ');
}

function compareRunbookItems(left: WorktreeCleanupRunbookItem, right: WorktreeCleanupRunbookItem): number {
  const laneDelta = laneOrder[left.lane] - laneOrder[right.lane];
  if (laneDelta) return laneDelta;

  const taskDelta = (left.task?.id ?? 'ZZZ').localeCompare(right.task?.id ?? 'ZZZ');
  if (taskDelta) return taskDelta;

  const branchDelta = left.branch.localeCompare(right.branch);
  if (branchDelta) return branchDelta;

  return left.path.localeCompare(right.path);
}

function compareRunbookGroups(
  left: WorktreeCleanupRunbookGroup,
  right: WorktreeCleanupRunbookGroup,
  kind: 'lane' | 'task' | 'state'
): number {
  if (kind === 'lane') {
    return laneOrder[left.id as WorktreeCleanupLane] - laneOrder[right.id as WorktreeCleanupLane];
  }

  const priorityDelta = groupPriority(left) - groupPriority(right);
  if (priorityDelta) return priorityDelta;

  return left.label.localeCompare(right.label);
}

function groupPriority(group: WorktreeCleanupRunbookGroup): number {
  return Math.min(...group.items.map((item) => laneOrder[item.lane]));
}

function highestPriorityTone(items: WorktreeCleanupRunbookItem[]): WorktreeCleanupRunbookTone {
  const sorted = [...items].sort(compareRunbookItems);
  return sorted[0]?.tone ?? 'review';
}

function isBlockedLane(lane: WorktreeCleanupLane): boolean {
  return lane === 'blocked-active-session' || lane === 'blocked-protected' || lane === 'blocked-locked';
}

function isReviewLane(lane: WorktreeCleanupLane): boolean {
  return lane === 'review-first' || lane === 'review-prunable' || lane === 'review-saved-workspace' || lane === 'review-confirmation';
}

function branchReviewDetail(aheadCount: number, behindCount: number): string {
  const parts = [];
  if (aheadCount > 0) parts.push(`${aheadCount} ahead`);
  if (behindCount > 0) parts.push(`${behindCount} behind`);
  return parts.length > 0 ? parts.join(' / ') : 'Branch needs review.';
}

function normalizePath(path: string): string {
  return path.trim().replace(/\/+$/g, '');
}

function pathBasename(path: string): string {
  return normalizePath(path).split('/').filter(Boolean).at(-1) ?? path;
}
