/**
 * Domain & wire types for the Left Sessions Rail (`sessions/rail` + `sessionRail` topic).
 * Mirrors `SessionsFeature.cs` and `desktop/src/features/sessions/rail_page.rs` 1:1.
 */

export type SessionMarkState = 'working' | 'done' | 'settled';
export type RailSort = 'recent' | 'name';
export type RailDir = 'desc' | 'asc';

export interface RailGroupBy {
  status: boolean;
  project: boolean;
}

export interface RailView {
  groupBy: RailGroupBy;
  sort: RailSort;
  dir: RailDir;
}

export interface RailFilters {
  provider: string[];
  status: SessionMarkState[];
  location: ('local' | 'remote')[];
}

export interface RailCursor {
  section: number;
  group: string;
  repoUrl: string;
  header: number;
  ordering: string;
}

export interface RailTotals {
  needs: number;
}

export interface ExpectedRowSource {
  routeMachine?: string | null;
  machine?: string | null;
  [key: string]: unknown;
}

export interface RailEntry {
  sessionId: string;
  kind: 'section' | 'project' | 'session' | string;
  section: string;
  agent: string;
  repoUrl: string;
  group: string;
  count: number;
  machines: string[];
  title?: string | null;
  machine?: string | null;
  updatedAt: number;
  completedAt?: number | null;
  running: boolean;
  failed: boolean;
  unread: boolean;
  branch?: string | null;
  icon: string;
  sectionIndex: number;
  header: number;
  ordering: string;
  preview?: string | null;
  parentSessionId?: string | null;
  depth: number;
  pinnedAt?: number | null;
  orchestratorAt?: number | null;
  position?: number | null;
  model?: string | null;
  worktree?: string | null;
  lastError?: string | null;
  resumeCommand?: string | null;
  lastSeenMs?: number | null;
  source?: ExpectedRowSource | null;
  nativeActivity: boolean;
  needsYou: boolean;
  statusKind?: string | null;
  statusText?: string | null;
  statusReason?: string | null;
  subagentCount?: number | null;
  subagentDone?: number | null;
  handle?: string | null;
}

export interface RailPage {
  totals: RailTotals;
  agents: string[];
  sessions: RailEntry[];
  next?: RailCursor | null;
  start?: number;
}

export interface RailArgs {
  providers: string[];
  locations: string[];
  statuses: string[];
  groupBy: RailGroupBy;
  sort: RailSort;
  dir: RailDir;
  folded: string[];
  filter: string | null;
  after: RailCursor | null;
  limit: number;
  anchor: string | null;
  above: number;
}

export interface RailChange {
  sessionId: string;
}

export type PresenceKind = 'needs_you' | 'working' | 'failed' | 'done' | 'unread' | 'idle';

export type RailListItem =
  | {
      kind: 'section';
      key: string;
      foldKey: string;
      label: string;
      count: number;
      folded: boolean;
    }
  | {
      kind: 'project';
      key: string;
      foldKey: string;
      label: string;
      repoUrl: string;
      machines: string[];
      count: number;
      folded: boolean;
    }
  | {
      kind: 'session';
      key: string;
      entry: RailEntry;
    };

export const DEFAULT_RAIL_VIEW: RailView = {
  groupBy: { status: false, project: true },
  sort: 'recent',
  dir: 'desc'
};

export const EMPTY_RAIL_FILTERS: RailFilters = {
  provider: [],
  status: [],
  location: []
};
