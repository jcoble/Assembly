/**
 * Pure view-model for the My Work rail. Components own drawing and interaction;
 * this module owns the durable option shape plus filtering, sorting and grouping.
 */
import type { OwnedSession } from '../ownedSessions.ts';

/** `manual` is the order a person dragged the rows into. */
export type MyWorkSort = 'recent' | 'name' | 'manual';
export type MyWorkSortDirection = 'asc' | 'desc';
export type MyWorkStatus = 'working' | 'done' | 'settled';

export interface MyWorkViewOptions {
  groupByProject: boolean;
  groupByStatus: boolean;
  sortBy: MyWorkSort;
  sortDirection: MyWorkSortDirection;
}

export interface MyWorkGroup {
  key: string;
  label: string;
  sessions: OwnedSession[];
  /** Project sections inside a status, when both groupings are on. */
  subgroups?: MyWorkGroup[];
  /** Helpers drawn under a row of this group, by the parent's owned id. */
  helpers?: Record<string, OwnedSession[]>;
}

/** Selected option values per filter group id; an empty list filters nothing. */
export type MyWorkFilters = Record<string, string[]>;

export const MY_WORK_STATUSES: readonly MyWorkStatus[] = ['working', 'done', 'settled'];
/**
 * Which way round each sort starts.
 *
 * Both directions are worth having, but only one of them is what a person means
 * by the sort's own name: "recent activity" means the newest at the top, and
 * "name" means A first. Picking a sort therefore returns to its own natural
 * direction rather than carrying over the other sort's.
 */
export const NATURAL_SORT_DIRECTION: Record<MyWorkSort, MyWorkSortDirection> = {
  recent: 'desc',
  name: 'asc',
  manual: 'asc'
};

/** For new installs only: a saved choice keeps its own values (normalizeMyWorkViewOptions). */
export const DEFAULT_MY_WORK_VIEW_OPTIONS: MyWorkViewOptions = {
  groupByProject: true,
  groupByStatus: false,
  sortBy: 'recent',
  sortDirection: NATURAL_SORT_DIRECTION.recent
};

/** The rail's filter pills: one source for the pills and for the predicate. */
export const MY_WORK_FILTER_GROUPS = [
  {
    id: 'provider',
    label: 'Provider',
    options: [
      { value: 'claude', label: 'Claude' },
      { value: 'codex', label: 'Codex' },
      { value: 'antigravity', label: 'Agy' }
    ]
  },
  {
    id: 'status',
    label: 'Status',
    options: [
      { value: 'working', label: 'Working' },
      { value: 'done', label: 'Done' },
      { value: 'settled', label: 'Settled' }
    ]
  },
  {
    id: 'location',
    label: 'Location',
    options: [
      { value: 'local', label: 'Local' },
      { value: 'remote', label: 'Remote' }
    ]
  }
];

/** What the direction control says it will do, for the sort in force. */
export function myWorkSortDirectionLabel(
  sortBy: MyWorkSort,
  direction: MyWorkSortDirection
): string {
  if (sortBy === 'manual') return 'Custom order';
  if (sortBy === 'name') return direction === 'asc' ? 'A to Z' : 'Z to A';
  return direction === 'desc' ? 'Newest first' : 'Oldest first';
}

const STATUS_LABELS: Record<MyWorkStatus, string> = {
  working: 'Working',
  done: 'Done',
  settled: 'Settled'
};

export function myWorkStatus(
  session: Pick<OwnedSession, 'completedAt' | 'settledAt'>
): MyWorkStatus {
  if (session.settledAt) return 'settled';
  if (session.completedAt) return 'done';
  return 'working';
}

/** The project group SQL gave the session: its key and the label the header shows. */
export function myWorkProject(
  session: Pick<OwnedSession, 'projectGroupKey' | 'projectGroupLabel'>
): { key: string; label: string } {
  return { key: session.projectGroupKey, label: session.projectGroupLabel };
}

function activityRank(session: OwnedSession): number {
  const stamp = session.lastActivity || session.settledAt || session.completedAt;
  if (!stamp) return Number.NEGATIVE_INFINITY;
  const parsed = Date.parse(stamp);
  return Number.isFinite(parsed) ? parsed : Number.NEGATIVE_INFINITY;
}

/** Options inside a group combine as OR; groups combine as AND. */
export function matchesMyWorkFilters(session: OwnedSession, filters: MyWorkFilters): boolean {
  const values: Record<string, string> = {
    provider: session.agent,
    status: myWorkStatus(session),
    location: session.executionEnvironment
  };
  return MY_WORK_FILTER_GROUPS.every((group) => {
    const chosen = filters[group.id] ?? [];
    return chosen.length === 0 || chosen.includes(values[group.id]);
  });
}

export function prepareMyWorkSessions(
  sessions: OwnedSession[],
  options: Pick<MyWorkViewOptions, 'sortBy'> & Partial<Pick<MyWorkViewOptions, 'sortDirection'>>,
  manualOrder: readonly string[] = []
): OwnedSession[] {
  const direction = options.sortDirection ?? NATURAL_SORT_DIRECTION[options.sortBy];
  const flip = direction === 'asc' ? 1 : -1;
  // Equal rows fall back to the session id, which a restart does not change;
  // the order the list arrives in does.
  const byId = (left: OwnedSession, right: OwnedSession) =>
    left.ownedId < right.ownedId ? -1 : left.ownedId > right.ownedId ? 1 : 0;

  if (options.sortBy === 'manual') {
    // A session with no saved place yet (-1) sits on top, newest first.
    const place = new Map(manualOrder.map((ownedId, index) => [ownedId, index]));
    const rank = (session: OwnedSession) => place.get(session.ownedId) ?? -1;
    return [...sessions].sort((left, right) =>
      rank(left) - rank(right)
      || activityRank(right) - activityRank(left)
      || byId(left, right));
  }

  return [...sessions].sort((left, right) => {
    // Compared one way round and turned over afterwards, so both directions
    // order equal rows the same.
    const ascending =
      options.sortBy === 'name'
        ? left.title.localeCompare(right.title, undefined, { sensitivity: 'base' })
        : activityRank(left) - activityRank(right);
    return ascending * flip || byId(left, right);
  });
}

function groupSessions(sessions: OwnedSession[], by: 'status' | 'project'): MyWorkGroup[] {
  const groups = new Map<string, MyWorkGroup>();
  for (const session of sessions) {
    const identity =
      by === 'status'
        ? { key: myWorkStatus(session), label: STATUS_LABELS[myWorkStatus(session)] }
        : myWorkProject(session);
    const group = groups.get(identity.key) ?? { ...identity, sessions: [] };
    group.sessions.push(session);
    groups.set(identity.key, group);
  }

  if (by === 'status') {
    return MY_WORK_STATUSES.flatMap((status) => {
      const group = groups.get(status);
      return group ? [group] : [];
    });
  }
  // "No project" goes last; projects read A to Z.
  return [...groups.values()].sort(
    (left, right) =>
      Number(left.key === 'none') - Number(right.key === 'none') ||
      left.label.localeCompare(right.label, undefined, { sensitivity: 'base' })
  );
}

export function buildMyWorkGroups(
  sessions: OwnedSession[],
  options: MyWorkViewOptions,
  manualOrder: readonly string[] = []
): MyWorkGroup[] {
  const prepared = prepareMyWorkSessions(sessions, options, manualOrder);
  // A helper sits under its parent's row, and a workflow run's workers in a
  // section of their own; a pinned one sits in Pinned instead. Each appears
  // once. A helper whose parent is not listed stays an ordinary row, and so does
  // a finished helper when the rail is grouped by status, so Done and Settled hold it.
  const listed = new Set(prepared.map((session) => session.ownedId));
  const helpers = new Map<string, OwnedSession[]>();
  const runs = new Map<string, MyWorkGroup>();
  const all: OwnedSession[] = [];
  for (const session of prepared) {
    if (session.pinnedAt) {
      all.push(session);
    } else if (session.helperOf && listed.has(session.helperOf) && !(options.groupByStatus && myWorkStatus(session) !== 'working')) {
      helpers.set(session.helperOf, [...(helpers.get(session.helperOf) ?? []), session]);
    } else if (session.runId) {
      const key = `run:${session.runId}`;
      const run = runs.get(key) ?? { key, label: session.runLabel ?? 'Workflow run', sessions: [] };
      run.sessions.push(session);
      runs.set(key, run);
    } else {
      all.push(session);
    }
  }
  // The owner's main orchestrator sits alone above everything. A machine that
  // could not be reached may still carry an older mark; the newest one wins.
  const orchestrator = all.reduce<OwnedSession | null>((newest, session) =>
    (session.orchestratorMarkedAtMs ?? 0) > (newest?.orchestratorMarkedAtMs ?? 0) ? session : newest, null);
  const sorted = orchestrator ? all.filter((session) => session !== orchestrator) : all;
  // Pinned sessions sit in their own section at the top, whatever their status.
  const pinned = sorted.filter((session) => session.pinnedAt);
  const unpinned = pinned.length ? sorted.filter((session) => !session.pinnedAt) : sorted;
  const groups: MyWorkGroup[] = [
    ...(orchestrator ? [{ key: 'orchestrator', label: 'Main orchestrator', sessions: [orchestrator] }] : []),
    ...(pinned.length ? [{ key: 'pinned', label: 'Pinned', sessions: pinned }] : []),
    ...groupUnpinned(unpinned, options),
    ...runs.values()
  ];
  return helpers.size ? groups.map((group) => withHelpers(group, helpers)) : groups;
}

function withHelpers(group: MyWorkGroup, helpers: Map<string, OwnedSession[]>): MyWorkGroup {
  const own = group.sessions.flatMap((session) => {
    const children = helpers.get(session.ownedId);
    return children ? [[session.ownedId, children] as const] : [];
  });
  return {
    ...group,
    ...(own.length ? { helpers: Object.fromEntries(own) } : {}),
    ...(group.subgroups ? { subgroups: group.subgroups.map((subgroup) => withHelpers(subgroup, helpers)) } : {})
  };
}

/** A group's rows and the helpers drawn under them: what its count and "needs you" mark cover. */
export function myWorkGroupSessions(group: MyWorkGroup): OwnedSession[] {
  return group.sessions.flatMap((session) => [session, ...(group.helpers?.[session.ownedId] ?? [])]);
}

function groupUnpinned(prepared: OwnedSession[], options: MyWorkViewOptions): MyWorkGroup[] {
  if (options.groupByProject && options.groupByStatus) {
    return groupSessions(prepared, 'status').map((status) => ({
      ...status,
      subgroups: groupSessions(status.sessions, 'project').map((project) => ({
        ...project,
        key: `${status.key}::${project.key}`
      }))
    }));
  }
  if (options.groupByProject) return groupSessions(prepared, 'project');
  if (options.groupByStatus) return groupSessions(prepared, 'status');
  return [{ key: 'all', label: '', sessions: prepared }];
}

/**
 * The saved custom order after `draggedId` is dropped before or after
 * `targetId`. `groupIds` is the dropped-into group as it is drawn. The group's
 * sessions are rewritten into the places they already hold, so every other
 * place is kept; group sessions with no place yet are added at the end.
 */
export function reorderMyWorkSessions(
  order: readonly string[],
  groupIds: readonly string[],
  draggedId: string,
  targetId: string,
  position: 'before' | 'after'
): string[] {
  if (draggedId === targetId || !groupIds.includes(draggedId) || !groupIds.includes(targetId)) {
    return [...order];
  }
  const moved = groupIds.filter((ownedId) => ownedId !== draggedId);
  const target = moved.indexOf(targetId);
  moved.splice(position === 'after' ? target + 1 : target, 0, draggedId);
  const inGroup = new Set(groupIds);
  const places = [...order, ...groupIds.filter((ownedId) => !order.includes(ownedId))];
  let next = 0;
  return places.map((ownedId) => (inGroup.has(ownedId) ? moved[next++] : ownedId));
}

function isSort(value: unknown): value is MyWorkSort {
  return value === 'recent' || value === 'name' || value === 'manual';
}

function isSortDirection(value: unknown): value is MyWorkSortDirection {
  return value === 'asc' || value === 'desc';
}

export function normalizeMyWorkViewOptions(value: unknown): MyWorkViewOptions {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return { ...DEFAULT_MY_WORK_VIEW_OPTIONS };
  }
  // A stored option from before the two toggles carries one `groupBy` value;
  // an old `visibleStatuses` is dropped because the status pill replaced it.
  const candidate = value as Partial<MyWorkViewOptions> & { groupBy?: unknown };
  const legacy = ['none', 'status', 'project'].includes(candidate.groupBy as string)
    ? candidate.groupBy
    : null;
  const sortBy = isSort(candidate.sortBy) ? candidate.sortBy : DEFAULT_MY_WORK_VIEW_OPTIONS.sortBy;
  return {
    groupByProject:
      typeof candidate.groupByProject === 'boolean'
        ? candidate.groupByProject
        : legacy ? legacy === 'project' : DEFAULT_MY_WORK_VIEW_OPTIONS.groupByProject,
    groupByStatus:
      typeof candidate.groupByStatus === 'boolean'
        ? candidate.groupByStatus
        : legacy ? legacy === 'status' : DEFAULT_MY_WORK_VIEW_OPTIONS.groupByStatus,
    sortBy,
    // A stored option from before the direction existed carries none, and the
    // sort it was saved with is the direction that sort means.
    sortDirection: isSortDirection(candidate.sortDirection)
      ? candidate.sortDirection
      : NATURAL_SORT_DIRECTION[sortBy]
  };
}

/** Keeps only known filter groups and their known option values, in option order. */
export function normalizeMyWorkFilters(value: unknown): MyWorkFilters {
  const candidate = value && typeof value === 'object' ? (value as Record<string, unknown>) : {};
  return Object.fromEntries(
    MY_WORK_FILTER_GROUPS.map((group) => {
      const stored = candidate[group.id];
      const chosen = Array.isArray(stored) ? stored : [];
      return [group.id, group.options.map((option) => option.value).filter((v) => chosen.includes(v))];
    })
  );
}
