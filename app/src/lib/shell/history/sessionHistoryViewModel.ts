import type { RepositoryCheckout } from '../../tauriSource.ts';
import {
  filterSessionLibrary,
  type SessionHistoryFilters,
  type SessionLibraryRecord
} from './library/sessionLibraryModel.ts';

export interface SessionHistoryFilterOptions {
  query?: string;
  provider?: string;
  windowState?: SessionHistoryWindowState;
  /** Local Git checkouts, used only to match local session folders. */
  checkouts?: Readonly<Record<string, readonly RepositoryCheckout[]>>;
}

export const SESSION_HISTORY_ROW_WINDOW = 25;

export interface SessionHistoryWindowState {
  query: string;
  provider: string;
  visibleRowsByWorktree: Readonly<Record<string, number>>;
}

export interface SessionHistoryProviderOption {
  value: string;
  label: string;
}

export interface SessionHistoryRow {
  record: SessionLibraryRecord;
  displayTitle: string;
  excerpt: string;
  providerLabel: string;
  statusHint: 'Live' | 'Open' | null;
  activityTime: number;
}

export interface SessionHistoryWorktreeGroup {
  key: string;
  name: string;
  path: string;
  count: number;
  activityTime: number;
  rows: SessionHistoryRow[];
  olderCount: number;
}

export interface SessionHistoryProjectGroup {
  key: string;
  machine: string;
  name: string;
  count: number;
  activityTime: number;
  singleCheckout: boolean;
  worktrees: SessionHistoryWorktreeGroup[];
}

export interface SessionHistoryViewModel {
  showMachines: boolean;
  machines: { key: string; projects: SessionHistoryProjectGroup[] }[];
  totalCount: number;
  providers: SessionHistoryProviderOption[];
  projects: SessionHistoryProjectGroup[];
}

/**
 * Which groups the reader has opened. The sets hold OPEN keys, so everything
 * starts collapsed. That default is load-bearing: this machine has hundreds of
 * past sessions, and mounting every card at once used to freeze the whole app
 * for seconds — the browser engine re-checks sibling styling on every inserted
 * node, which grows quadratic in a long flat list. Opening one group at a time
 * keeps each mount small.
 */
export interface SessionHistoryCollapseState {
  projects: ReadonlySet<string>;
  worktrees: ReadonlySet<string>;
}

type SessionHistoryGroupLevel = keyof SessionHistoryCollapseState;

interface RowIdentity {
  projectKey: string;
  projectName: string;
  worktreePath: string;
  worktreeName: string;
}

interface IndexedRow extends SessionHistoryRow, RowIdentity {
  searchText: string;
}

function normalizedWindowFilters(
  options: SessionHistoryFilterOptions
): Pick<SessionHistoryWindowState, 'query' | 'provider'> {
  return {
    query: compactText(options.query).toLocaleLowerCase(),
    provider: compactText(options.provider).toLocaleLowerCase()
  };
}

export function createSessionHistoryWindowState(
  options: SessionHistoryFilterOptions = {}
): SessionHistoryWindowState {
  return { ...normalizedWindowFilters(options), visibleRowsByWorktree: {} };
}

export function resetSessionHistoryWindowOnFilterChange(
  state: SessionHistoryWindowState,
  options: SessionHistoryFilterOptions
): SessionHistoryWindowState {
  const next = normalizedWindowFilters(options);
  if (state.query === next.query && state.provider === next.provider) return state;
  return { ...next, visibleRowsByWorktree: {} };
}

export function extendSessionHistoryWindow(
  state: SessionHistoryWindowState,
  worktreeKey: string
): SessionHistoryWindowState {
  const visible = state.visibleRowsByWorktree[worktreeKey] ?? SESSION_HISTORY_ROW_WINDOW;
  return {
    ...state,
    visibleRowsByWorktree: {
      ...state.visibleRowsByWorktree,
      [worktreeKey]: visible + SESSION_HISTORY_ROW_WINDOW
    }
  };
}

function canonicalPath(value: string | null | undefined): string {
  const raw = (value ?? '').trim().replaceAll('\\', '/').replace(/\/+/g, '/');
  if (!raw || raw === '/') return raw;
  return raw.replace(/\/$/, '');
}

/** Apply history scopes: Workspace is the active folder, Project the active session's group. */
export function filterSessionHistoryRecords(
  records: readonly SessionLibraryRecord[],
  filters: SessionHistoryFilters = {}
): SessionLibraryRecord[] {
  const { scope = 'all', machine = 'local', workspacePath, projectKey, ...libraryFilters } = filters;
  const filtered = filterSessionLibrary(records, libraryFilters);
  if (scope === 'workspace') {
    const workspace = canonicalPath(workspacePath);
    return workspace
      ? filtered.filter((record) => record.machine === machine && canonicalPath(record.canonicalCwd) === workspace)
      : [];
  }
  if (scope === 'project') {
    return projectKey ? filtered.filter((record) => record.machine === machine && record.projectGroupKey === projectKey) : [];
  }
  return filtered;
}

function pathName(path: string, fallback: string): string {
  return path.split('/').filter(Boolean).at(-1) || fallback;
}

function uniqueCheckoutName(path: string, paths: string[]): string {
  const parts = path.split('/').filter(Boolean);
  for (let length = 1; length <= parts.length; length += 1) {
    const suffix = parts.slice(-length).join('/');
    if (paths.every((other) => other === path || (other !== suffix && !other.endsWith(`/${suffix}`)))) return suffix;
  }
  return path;
}

function identifyRow(record: SessionLibraryRecord): RowIdentity {
  const worktreePath = canonicalPath(record.canonicalCwd) || 'Unknown checkout';
  return {
    projectKey: record.projectGroupKey,
    projectName: record.projectGroupLabel,
    worktreePath,
    worktreeName: pathName(worktreePath, 'Unknown checkout')
  };
}

function compactText(value: string | null | undefined): string {
  return (value ?? '').replace(/\s+/g, ' ').trim();
}

function firstUsefulExcerpt(record: SessionLibraryRecord): string {
  return compactText(
    record.firstPrompt
      || record.latestTurns.find((turn) => turn.speaker === 'user')?.text
      || record.description
      || record.latestTurns[0]?.text
  ).replace(/^(?:you|user|agent):\s*/i, '');
}

function displayTitleFor(record: SessionLibraryRecord, excerpt: string): string {
  const title = compactText(record.title);
  const titleIsIdentifier = Boolean(
    title
      && (title === record.nativeSessionId || title === record.ownedId || title === record.key)
  );
  if (title && !titleIsIdentifier) return title;
  return excerpt || title || 'Untitled session';
}

function providerLabelFor(provider: string): string {
  const value = compactText(provider);
  if (!value) return 'Unknown';
  return value
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part) => `${part[0]?.toUpperCase() ?? ''}${part.slice(1)}`)
    .join(' ');
}

/** Runtime states of a session whose agent is running. */
export const runningRuntimeStates = new Set(['starting', 'ready', 'working', 'waiting-approval', 'waiting-input', 'interrupting']);

function statusHintFor(record: SessionLibraryRecord): 'Live' | 'Open' | null {
  if (record.source !== 'owned' || !record.owned) return null;
  if (record.owned.state === 'live' || (record.runtimeState && runningRuntimeStates.has(record.runtimeState))) {
    return 'Live';
  }
  return record.owned.state === 'background' ? 'Open' : null;
}

function timestamp(value: string | null): number {
  const parsed = value ? Date.parse(value) : Number.NaN;
  return Number.isFinite(parsed) ? parsed : 0;
}

function indexRow(record: SessionLibraryRecord): IndexedRow {
  const paths = identifyRow(record);
  const excerpt = firstUsefulExcerpt(record);
  const displayTitle = displayTitleFor(record, excerpt);
  return {
    record,
    ...paths,
    displayTitle,
    excerpt,
    providerLabel: providerLabelFor(record.provider),
    statusHint: statusHintFor(record),
    activityTime: timestamp(record.updatedAt),
    searchText: [
      displayTitle,
      excerpt,
      record.title,
      record.description,
      record.firstPrompt,
      ...record.latestTurns.map((turn) => turn.text),
      paths.projectName,
      paths.worktreeName,
      paths.worktreePath
    ]
      .map(compactText)
      .filter(Boolean)
      .join(' ')
      .toLocaleLowerCase()
  };
}

function compareRows(left: SessionHistoryRow, right: SessionHistoryRow): number {
  return right.activityTime - left.activityTime || left.displayTitle.localeCompare(right.displayTitle);
}

function compareGroups(
  left: Pick<SessionHistoryProjectGroup, 'activityTime' | 'name'>,
  right: Pick<SessionHistoryProjectGroup, 'activityTime' | 'name'>
): number {
  return right.activityTime - left.activityTime || left.name.localeCompare(right.name);
}

export function buildSessionHistoryViewModel(
  records: readonly SessionLibraryRecord[],
  options: SessionHistoryFilterOptions = {}
): SessionHistoryViewModel {
  const indexed = records.map(indexRow);
  const providers = [...new Set(indexed.map((row) => row.record.provider).filter(Boolean))]
    .sort((left, right) => providerLabelFor(left).localeCompare(providerLabelFor(right)))
    .map((value) => ({ value, label: providerLabelFor(value) }));
  const { query, provider } = normalizedWindowFilters(options);
  const windowMatchesFilters = options.windowState?.query === query
    && options.windowState.provider === provider;
  const filtered = indexed.filter((row) => {
    if (query && !row.searchText.includes(query)) return false;
    return !provider || row.record.provider.toLocaleLowerCase() === provider;
  });

  const projects = new Map<string, {
    name: string;
    machine: string;
    worktrees: Map<string, { name: string; path: string; rows: SessionHistoryRow[] }>;
  }>();

  const known = options.checkouts ?? {};
  for (const row of filtered) {
    const live = row.record.machine === 'local' ? known[row.projectKey] : undefined;
    const projectKey = row.record.machine === 'local' ? row.projectKey : JSON.stringify([row.record.machine, row.projectKey]);
    // A session can run in a folder BELOW its checkout root. Match the longest
    // live checkout prefix so `/repo/packages/app` is filed under `/repo`, not
    // presented as a vanished worktree. If the checkout really was deleted,
    // keep its historical row; History is still useful even when Git no longer
    // lists the folder.
    const checkoutPath = live
      ?.map((checkout) => canonicalPath(checkout.path))
      .filter((path) => row.worktreePath === path || row.worktreePath.startsWith(`${path}/`))
      .toSorted((left, right) => right.length - left.length)[0];
    const worktreePath = checkoutPath ?? row.worktreePath;
    const worktreeName = pathName(worktreePath, row.worktreeName);

    let project = projects.get(projectKey);
    if (!project) {
      project = { name: row.projectName, machine: row.record.machine, worktrees: new Map() };
      projects.set(projectKey, project);
    }

    const worktreeKey = JSON.stringify([projectKey, worktreePath]);
    let worktree = project.worktrees.get(worktreeKey);
    if (!worktree) {
      worktree = { name: worktreeName, path: worktreePath, rows: [] };
      project.worktrees.set(worktreeKey, worktree);
    }
    worktree.rows.push(row);
  }

  // Groups read newest first, with "No project" last as in the rail.
  const projectGroups = [...projects.entries()].map(([key, project]): SessionHistoryProjectGroup => {
    const worktrees = [...project.worktrees.entries()]
      .map(([worktreeKey, worktree]): SessionHistoryWorktreeGroup => {
        const allRows = worktree.rows.toSorted(compareRows);
        const visibleRowCount = query
          ? allRows.length
          : windowMatchesFilters
            ? options.windowState?.visibleRowsByWorktree[worktreeKey] ?? SESSION_HISTORY_ROW_WINDOW
            : SESSION_HISTORY_ROW_WINDOW;
        const rows = allRows.slice(0, visibleRowCount);
        return {
          key: worktreeKey,
          name: uniqueCheckoutName(worktree.path, [...project.worktrees.values()].map((tree) => tree.path)),
          path: worktree.path,
          count: allRows.length,
          activityTime: allRows[0]?.activityTime ?? 0,
          rows,
          olderCount: Math.max(0, allRows.length - rows.length)
        };
      })
      .toSorted(compareGroups);
    return {
      key,
      name: project.name,
      machine: project.machine,
      count: worktrees.reduce((total, worktree) => total + worktree.count, 0),
      activityTime: worktrees[0]?.activityTime ?? 0,
      singleCheckout: worktrees.length === 1,
      worktrees
    };
  }).toSorted((left, right) => Number(left.key === 'none') - Number(right.key === 'none') || compareGroups(left, right));

  // Counted from what was placed, not from what passed the filters: a session
  // whose checkout has been deleted is left out above, and a header claiming it
  // was still there would be wrong by exactly that many.
  const totalCount = projectGroups.reduce((total, project) => total + project.count, 0);
  const machineKeys = [...new Set(projectGroups.map((project) => project.machine))]
    .sort((a, b) => Number(b === 'local') - Number(a === 'local') || a.localeCompare(b));
  const machines = machineKeys.map((key) => ({ key, projects: projectGroups.filter((project) => project.machine === key) }));
  return { totalCount, providers, projects: machines.flatMap((machine) => machine.projects), machines, showMachines: machines.length > 1 };
}

export function createSessionHistoryCollapseState(): SessionHistoryCollapseState {
  return { projects: new Set(), worktrees: new Set() };
}

export function isSessionHistoryGroupOpen(
  state: SessionHistoryCollapseState,
  level: 'project' | 'worktree',
  key: string
): boolean {
  return state[`${level}s` as SessionHistoryGroupLevel].has(key);
}

export function toggleSessionHistoryGroup(
  state: SessionHistoryCollapseState,
  level: 'project' | 'worktree',
  key: string
): SessionHistoryCollapseState {
  const collection = `${level}s` as SessionHistoryGroupLevel;
  const next = new Set(state[collection]);
  if (next.has(key)) next.delete(key);
  else next.add(key);
  return { ...state, [collection]: next };
}

export function visibleSessionHistoryRows(
  projects: readonly SessionHistoryProjectGroup[],
  state: SessionHistoryCollapseState
): SessionHistoryRow[] {
  const rows: SessionHistoryRow[] = [];
  for (const project of projects) {
    if (!isSessionHistoryGroupOpen(state, 'project', project.key)) continue;
    for (const worktree of project.worktrees) {
      if (!project.singleCheckout && !isSessionHistoryGroupOpen(state, 'worktree', worktree.key)) continue;
      rows.push(...worktree.rows);
    }
  }
  return rows;
}

// Backend readers: src-tauri/src/agent_conversation/history/transcript/{codex,claude}.rs.
export function hasTranscriptReader(provider: string): provider is 'codex' | 'claude' {
  return ['codex', 'claude'].includes(provider);
}
