import type { ProjectRoot } from './sourceData.ts';
import { normalizeProvider, type AgentKind } from './shell/ownedSessions.ts';
import { normalizeBrowserUrl } from './shell/browser/normalizeBrowserUrl.ts';
import { createDefaultSourceDockLayout, normalizeSourceDockLayout, type SourceDockLayout } from './sourceDockLayout.ts';

export const snapshotStorageKey = 'mac-command-bar.workspace-snapshots';

export type WorkspaceSnapshotProvider = Exclude<AgentKind, 'other'> | 'cmux' | 'manual';
export type WorkspaceSnapshotActivityMode =
  | 'files'
  | 'clipboard'
  | 'conversations'
  | 'sessions'
  | 'agents'
  | 'worktrees'
  | 'git'
  | 'runs';
export type WorkspaceSnapshotTerminalApp =
  | 'Warp'
  | 'Terminal'
  | 'iTerm'
  | 'iTerm2'
  | 'Ghostty'
  | 'WezTerm'
  | 'Alacritty';
export type WorkspaceSnapshotContextPanelMode = 'grid' | 'stack';
export type WorkspaceSnapshotContextPanelPlacement = 'top' | 'side' | 'bottom';
export type WorkspaceSnapshotSidePanePosition = 'left' | 'right';
export type WorkspaceSnapshotContextCardID = 'orchestration' | 'runtime' | 'agents' | 'worktrees' | 'repo';
export type WorkspaceSnapshotIntelligencePanel = 'problems' | 'symbols' | 'git';

export type WorkspaceSnapshotViewState = {
  contextPanelMode: WorkspaceSnapshotContextPanelMode;
  contextPanelPlacement: WorkspaceSnapshotContextPanelPlacement;
  contextPanelCollapsed: boolean;
  editorInsightCollapsed: boolean;
  sidePanePosition: WorkspaceSnapshotSidePanePosition;
  sidePaneWidth: number;
  contextPaneWidth: number;
  contextPaneHeight: number;
  editorInsightWidth: number;
  sourceChromeCompact: boolean;
  sourceActivityFilter: string;
  hiddenContextCardIDs: WorkspaceSnapshotContextCardID[];
  activeContextCardID: WorkspaceSnapshotContextCardID;
  sourceIntelligencePanel: WorkspaceSnapshotIntelligencePanel;
};

export type WorkspaceSnapshotEmbeddedTerminal = {
  sessionID: string;
  cwd: string;
  shell: string | null;
  startedAt: number | null;
};

export type WorkspaceSnapshot = {
  id: string;
  provider: WorkspaceSnapshotProvider;
  sessionID: string;
  title: string;
  model: string | null;
  project: ProjectRoot;
  cwd: string;
  worktreePath: string | null;
  branch: string | null;
  selectedPath: string | null;
  selectedLine: number | null;
  openPaths: string[];
  sourceActivityMode: WorkspaceSnapshotActivityMode;
  sourceTerminalApp: WorkspaceSnapshotTerminalApp;
  browserUrl: string | null;
  viewState: WorkspaceSnapshotViewState;
  embeddedTerminal: WorkspaceSnapshotEmbeddedTerminal | null;
  dockLayout: SourceDockLayout;
  resumeCommand: string | null;
  capturedAt: number;
};

export type WorkspaceSnapshotInput = {
  provider: WorkspaceSnapshotProvider;
  sessionID: string;
  title: string;
  model?: string | null;
  project: ProjectRoot;
  cwd: string;
  worktreePath?: string | null;
  branch?: string | null;
  selectedPath?: string | null;
  selectedLine?: number | null;
  openPaths?: string[];
  sourceActivityMode?: WorkspaceSnapshotActivityMode;
  sourceTerminalApp?: WorkspaceSnapshotTerminalApp;
  browserUrl?: string | null;
  viewState?: Partial<WorkspaceSnapshotViewState> | null;
  embeddedTerminal?: WorkspaceSnapshotEmbeddedTerminal | null;
  dockLayout?: SourceDockLayout;
  resumeCommand?: string | null;
  capturedAt?: number;
};

export type RestoredWorkspaceSnapshot = {
  selectedProjectID: string;
  selectedSourcePaths: Record<string, string>;
  selectedPath: string | null;
  selectedLine: number | null;
  sourceActivityMode: WorkspaceSnapshotActivityMode;
  sourceTerminalApp: WorkspaceSnapshotTerminalApp;
  viewState: WorkspaceSnapshotViewState;
  cwd: string;
  worktreePath: string | null;
  branch: string | null;
  openPaths: string[];
  browserUrl: string | null;
  embeddedTerminal: WorkspaceSnapshotEmbeddedTerminal | null;
  dockLayout: SourceDockLayout;
  resumeCommand: string | null;
};

export type WorkspaceSnapshotRestoreReadinessKind =
  | 'live-terminal'
  | 'ready'
  | 'needs-terminal'
  | 'missing-worktree'
  | 'files-only';

export type WorkspaceSnapshotRestoreReadinessTone = 'ready' | 'warning' | 'blocked' | 'neutral';

export type WorkspaceSnapshotRestoreReadinessContext = {
  liveTerminalSessionIDs?: string[];
  knownWorktreePaths?: string[];
};

export type WorkspaceSnapshotRestoreReadiness = {
  kind: WorkspaceSnapshotRestoreReadinessKind;
  tone: WorkspaceSnapshotRestoreReadinessTone;
  label: string;
  detail: string;
  repairLabel?: string;
  repairDetail?: string;
  canRestoreWorkspace: boolean;
  canResumeEmbedded: boolean;
};

export type StartupWorkspaceSnapshotContext = {
  activeSessionKey?: string | null;
  selectedProjectID?: string | null;
  selectedProjectPath?: string | null;
};

export function createWorkspaceSnapshot(input: WorkspaceSnapshotInput): WorkspaceSnapshot {
  const project = normalizeProjectRoot(input.project);
  const selectedPath = normalizeOptionalPath(input.selectedPath);

  return {
    id: snapshotID(input.provider, input.sessionID),
    provider: input.provider,
    sessionID: input.sessionID.trim(),
    title: input.title.trim() || 'Untitled conversation',
    model: normalizeOptionalString(input.model),
    project,
    cwd: normalizePath(input.cwd || project.path),
    worktreePath: normalizeOptionalPath(input.worktreePath),
    branch: normalizeOptionalString(input.branch),
    selectedPath,
    selectedLine: normalizeLine(input.selectedLine),
    openPaths: normalizeOpenPaths(input.openPaths, selectedPath),
    sourceActivityMode: input.sourceActivityMode ?? 'conversations',
    sourceTerminalApp: input.sourceTerminalApp ?? 'Warp',
    browserUrl: normalizeBrowserUrl(input.browserUrl ?? '') || null,
    viewState: normalizeWorkspaceSnapshotViewState(input.viewState),
    embeddedTerminal: normalizeEmbeddedTerminal(input.embeddedTerminal),
    dockLayout: normalizeSourceDockLayout(input.dockLayout ?? createDefaultSourceDockLayout()),
    resumeCommand: normalizeOptionalString(input.resumeCommand),
    capturedAt: normalizeCapturedAt(input.capturedAt)
  };
}

export function restoreWorkspaceSnapshot(snapshot: WorkspaceSnapshot): RestoredWorkspaceSnapshot {
  const selectedSourcePaths =
    snapshot.selectedPath === null ? {} : { [snapshot.project.id]: snapshot.selectedPath };

  return {
    selectedProjectID: snapshot.project.id,
    selectedSourcePaths,
    selectedPath: snapshot.selectedPath,
    selectedLine: snapshot.selectedLine,
    sourceActivityMode: snapshot.sourceActivityMode,
    sourceTerminalApp: snapshot.sourceTerminalApp,
    viewState: normalizeWorkspaceSnapshotViewState(snapshot.viewState),
    cwd: snapshot.cwd,
    worktreePath: snapshot.worktreePath,
    branch: snapshot.branch,
    openPaths: snapshot.openPaths,
    browserUrl: normalizeBrowserUrl(snapshot.browserUrl ?? '') || null,
    embeddedTerminal: normalizeEmbeddedTerminal(snapshot.embeddedTerminal),
    dockLayout: normalizeSourceDockLayout(snapshot.dockLayout),
    resumeCommand: snapshot.resumeCommand
  };
}

export function describeWorkspaceSnapshotRestoreReadiness(
  snapshot: WorkspaceSnapshot,
  context: WorkspaceSnapshotRestoreReadinessContext = {}
): WorkspaceSnapshotRestoreReadiness {
  const liveTerminalSessionIDs = new Set((context.liveTerminalSessionIDs ?? []).map((id) => id.trim()).filter(Boolean));
  const knownWorktreePaths = normalizePathList(context.knownWorktreePaths);
  const normalizedWorktreePath = snapshot.worktreePath ? normalizePath(snapshot.worktreePath) : null;
  const savedTerminal = snapshot.embeddedTerminal;

  if (
    savedTerminal &&
    liveTerminalSessionIDs.has(savedTerminal.sessionID)
  ) {
    return {
      kind: 'live-terminal',
      tone: 'ready',
      label: 'Live terminal',
      detail: 'Can reattach to the saved embedded terminal session.',
      canRestoreWorkspace: true,
      canResumeEmbedded: true
    };
  }

  if (
    normalizedWorktreePath &&
    knownWorktreePaths.length > 0 &&
    !knownWorktreePaths.includes(normalizedWorktreePath)
  ) {
    return {
      kind: 'missing-worktree',
      tone: 'blocked',
      label: 'Worktree missing',
      detail: 'Saved worktree is not in the current worktree scan; restore files cautiously.',
      repairLabel: 'Copy repair plan',
      repairDetail: 'Audit Git worktree metadata, prune stale registrations, or recreate the saved worktree before resuming terminal commands.',
      canRestoreWorkspace: true,
      canResumeEmbedded: false
    };
  }

  if (normalizedWorktreePath && (knownWorktreePaths.length === 0 || knownWorktreePaths.includes(normalizedWorktreePath))) {
    return {
      kind: 'ready',
      tone: 'ready',
      label: 'Worktree ready',
      detail: 'Saved worktree is still registered and can be restored.',
      canRestoreWorkspace: true,
      canResumeEmbedded: true
    };
  }

  if (snapshot.resumeCommand) {
    return {
      kind: 'needs-terminal',
      tone: 'warning',
      label: 'Needs terminal',
      detail: 'Restore can start a new embedded terminal and run the saved command.',
      canRestoreWorkspace: true,
      canResumeEmbedded: true
    };
  }

  return {
    kind: 'files-only',
    tone: 'neutral',
    label: 'Files only',
    detail: 'Restores panes, selected file, and open tabs; no resume command was saved.',
    canRestoreWorkspace: true,
    canResumeEmbedded: false
  };
}

export function selectStartupWorkspaceSnapshot(
  snapshots: WorkspaceSnapshot[],
  context: StartupWorkspaceSnapshotContext = {}
): WorkspaceSnapshot | null {
  const sortedSnapshots = [...snapshots].sort((left, right) => right.capturedAt - left.capturedAt);
  const activeSessionKey = normalizeOptionalString(context.activeSessionKey);
  if (activeSessionKey) {
    const activeSnapshot = sortedSnapshots.find((snapshot) => snapshot.id === activeSessionKey);
    if (activeSnapshot) return activeSnapshot;
  }

  const selectedProjectID = normalizeOptionalString(context.selectedProjectID);
  const selectedProjectPath = normalizeOptionalPath(context.selectedProjectPath);
  if (!selectedProjectID && !selectedProjectPath) return null;

  return sortedSnapshots.find((snapshot) => {
    if (selectedProjectID && snapshot.project.id === selectedProjectID) return true;
    return Boolean(
      selectedProjectPath &&
        normalizePath(snapshot.project.path) === selectedProjectPath
    );
  }) ?? null;
}

export function upsertWorkspaceSnapshot(
  snapshots: WorkspaceSnapshot[],
  snapshot: WorkspaceSnapshot,
  maxSnapshots = 24
): WorkspaceSnapshot[] {
  const nextSnapshots = [
    snapshot,
    ...snapshots.filter((candidate) => candidate.id !== snapshot.id)
  ].sort((left, right) => right.capturedAt - left.capturedAt);

  return nextSnapshots.slice(0, Math.max(1, Math.floor(maxSnapshots)));
}

export function workspaceSnapshotsForWorktreePath(
  snapshots: WorkspaceSnapshot[],
  worktreePath: string | null | undefined,
  limit = 6
): WorkspaceSnapshot[] {
  const normalizedWorktreePath = normalizeOptionalPath(worktreePath);
  const maxSnapshots = Math.max(0, Math.floor(limit));
  if (!normalizedWorktreePath || maxSnapshots === 0) return [];

  return snapshots
    .filter((snapshot) => snapshotMatchesWorktreePath(snapshot, normalizedWorktreePath))
    .sort((left, right) => right.capturedAt - left.capturedAt)
    .slice(0, maxSnapshots);
}

export function parseStoredWorkspaceSnapshot(value: unknown): WorkspaceSnapshot | null {
  if (typeof value !== 'object' || value === null) return null;
  const snapshot = value as Partial<WorkspaceSnapshot>;
  if (
    !isWorkspaceSnapshotProvider(snapshot.provider) ||
    typeof snapshot.sessionID !== 'string' ||
    typeof snapshot.title !== 'string' ||
    typeof snapshot.cwd !== 'string' ||
    !isProjectRootLike(snapshot.project)
  ) {
    return null;
  }

  return createWorkspaceSnapshot({
    provider: snapshot.provider,
    sessionID: snapshot.sessionID,
    title: snapshot.title,
    model: typeof snapshot.model === 'string' ? snapshot.model : null,
    project: snapshot.project,
    cwd: snapshot.cwd,
    worktreePath: typeof snapshot.worktreePath === 'string' ? snapshot.worktreePath : null,
    branch: typeof snapshot.branch === 'string' ? snapshot.branch : null,
    selectedPath: typeof snapshot.selectedPath === 'string' ? snapshot.selectedPath : null,
    selectedLine: typeof snapshot.selectedLine === 'number' ? snapshot.selectedLine : null,
    openPaths: Array.isArray(snapshot.openPaths)
      ? snapshot.openPaths.filter((path): path is string => typeof path === 'string')
      : [],
    sourceActivityMode: isWorkspaceSnapshotActivityMode(snapshot.sourceActivityMode)
      ? snapshot.sourceActivityMode
      : 'conversations',
    sourceTerminalApp: isWorkspaceSnapshotTerminalApp(snapshot.sourceTerminalApp)
      ? snapshot.sourceTerminalApp
      : 'Warp',
    browserUrl: typeof snapshot.browserUrl === 'string' ? snapshot.browserUrl : null,
    viewState: normalizeWorkspaceSnapshotViewState(snapshot.viewState),
    embeddedTerminal: isWorkspaceSnapshotEmbeddedTerminal(snapshot.embeddedTerminal)
      ? snapshot.embeddedTerminal
      : null,
    dockLayout: snapshot.dockLayout,
    resumeCommand: typeof snapshot.resumeCommand === 'string' ? snapshot.resumeCommand : null,
    capturedAt: typeof snapshot.capturedAt === 'number' ? snapshot.capturedAt : Date.now()
  });
}

function snapshotID(provider: WorkspaceSnapshotProvider, sessionID: string): string {
  return `${provider}:${sessionID.trim() || 'session'}`;
}

function snapshotMatchesWorktreePath(snapshot: WorkspaceSnapshot, normalizedWorktreePath: string): boolean {
  return [
    snapshot.worktreePath,
    snapshot.cwd,
    snapshot.selectedPath,
    ...snapshot.openPaths
  ].some((path) => pathIsInsideWorktree(path, normalizedWorktreePath));
}

function pathIsInsideWorktree(path: string | null | undefined, normalizedWorktreePath: string): boolean {
  const normalizedPath = normalizeOptionalPath(path);
  if (!normalizedPath) return false;

  return normalizedPath === normalizedWorktreePath || normalizedPath.startsWith(`${normalizedWorktreePath}/`);
}

function isWorkspaceSnapshotProvider(value: unknown): value is WorkspaceSnapshotProvider {
  return typeof value === 'string' && (value === 'cmux' || value === 'manual' || (value !== 'other' && normalizeProvider(value).agent === value));
}

function isWorkspaceSnapshotActivityMode(value: unknown): value is WorkspaceSnapshotActivityMode {
  return (
    value === 'files' ||
    value === 'clipboard' ||
    value === 'conversations' ||
    value === 'sessions' ||
    value === 'agents' ||
    value === 'worktrees' ||
    value === 'git' ||
    value === 'runs'
  );
}

function isWorkspaceSnapshotTerminalApp(value: unknown): value is WorkspaceSnapshotTerminalApp {
  return (
    value === 'Warp' ||
    value === 'Terminal' ||
    value === 'iTerm' ||
    value === 'iTerm2' ||
    value === 'Ghostty' ||
    value === 'WezTerm' ||
    value === 'Alacritty'
  );
}

function isWorkspaceSnapshotContextPanelMode(value: unknown): value is WorkspaceSnapshotContextPanelMode {
  return value === 'grid' || value === 'stack';
}

function isWorkspaceSnapshotContextPanelPlacement(
  value: unknown
): value is WorkspaceSnapshotContextPanelPlacement {
  return value === 'top' || value === 'side' || value === 'bottom';
}

function isWorkspaceSnapshotSidePanePosition(value: unknown): value is WorkspaceSnapshotSidePanePosition {
  return value === 'left' || value === 'right';
}

function isWorkspaceSnapshotContextCardID(value: unknown): value is WorkspaceSnapshotContextCardID {
  return (
    value === 'orchestration' ||
    value === 'runtime' ||
    value === 'agents' ||
    value === 'worktrees' ||
    value === 'repo'
  );
}

function normalizeWorkspaceSnapshotViewState(
  value: Partial<WorkspaceSnapshotViewState> | null | undefined
): WorkspaceSnapshotViewState {
  const candidate = typeof value === 'object' && value !== null ? value : {};
  const hiddenContextCardIDs = Array.isArray(candidate.hiddenContextCardIDs)
    ? [...new Set(candidate.hiddenContextCardIDs.filter(isWorkspaceSnapshotContextCardID))]
    : [];

  return {
    contextPanelMode: isWorkspaceSnapshotContextPanelMode(candidate.contextPanelMode)
      ? candidate.contextPanelMode
      : 'grid',
    contextPanelPlacement: isWorkspaceSnapshotContextPanelPlacement(candidate.contextPanelPlacement)
      ? candidate.contextPanelPlacement
      : 'top',
    contextPanelCollapsed:
      typeof candidate.contextPanelCollapsed === 'boolean'
        ? candidate.contextPanelCollapsed
        : false,
    editorInsightCollapsed:
      typeof candidate.editorInsightCollapsed === 'boolean'
        ? candidate.editorInsightCollapsed
        : true,
    sidePanePosition: isWorkspaceSnapshotSidePanePosition(candidate.sidePanePosition)
      ? candidate.sidePanePosition
      : 'left',
    sidePaneWidth: normalizeWorkspaceSnapshotDimension(candidate.sidePaneWidth, 407, 40, 1600),
    contextPaneWidth: normalizeWorkspaceSnapshotDimension(candidate.contextPaneWidth, 330, 34, 1600),
    contextPaneHeight: normalizeWorkspaceSnapshotDimension(candidate.contextPaneHeight, 260, 96, 1100),
    editorInsightWidth: normalizeWorkspaceSnapshotDimension(candidate.editorInsightWidth, 260, 96, 1200),
    sourceChromeCompact:
      typeof candidate.sourceChromeCompact === 'boolean'
        ? candidate.sourceChromeCompact
        : true,
    sourceActivityFilter:
      typeof candidate.sourceActivityFilter === 'string'
        ? candidate.sourceActivityFilter.trim()
        : '',
    hiddenContextCardIDs,
    activeContextCardID: isWorkspaceSnapshotContextCardID(candidate.activeContextCardID)
      ? candidate.activeContextCardID
      : 'orchestration',
    // The Insights panel now only renders the Git body, so any persisted value
    // (including legacy 'problems'/'symbols') normalizes to 'git' on read — restore
    // lands on the surviving tab. The field + its union type stay for byte-compatible
    // parsing; old snapshots still parse without error.
    sourceIntelligencePanel: 'git'
  };
}

function normalizeWorkspaceSnapshotDimension(
  value: unknown,
  fallback: number,
  minimum: number,
  maximum: number
): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback;
  return Math.min(maximum, Math.max(minimum, Math.round(value)));
}

function isWorkspaceSnapshotEmbeddedTerminal(value: unknown): value is WorkspaceSnapshotEmbeddedTerminal {
  if (typeof value !== 'object' || value === null) return false;
  const terminal = value as Partial<WorkspaceSnapshotEmbeddedTerminal>;
  return (
    typeof terminal.sessionID === 'string' &&
    typeof terminal.cwd === 'string' &&
    (terminal.shell === null || terminal.shell === undefined || typeof terminal.shell === 'string') &&
    (terminal.startedAt === null || terminal.startedAt === undefined || typeof terminal.startedAt === 'number')
  );
}

function isProjectRootLike(value: unknown): value is ProjectRoot {
  if (typeof value !== 'object' || value === null) return false;
  const project = value as Partial<ProjectRoot>;
  return (
    typeof project.id === 'string' &&
    typeof project.name === 'string' &&
    typeof project.path === 'string'
  );
}

function normalizeProjectRoot(project: ProjectRoot): ProjectRoot {
  return {
    id: project.id.trim(),
    name: project.name.trim() || 'Project',
    path: normalizePath(project.path)
  };
}

function normalizeOpenPaths(openPaths: string[] | undefined, selectedPath: string | null): string[] {
  const seen = new Set<string>();
  const normalizedPaths = [
    selectedPath,
    ...(openPaths ?? []).map((path) => normalizeOptionalPath(path))
  ].filter((path): path is string => path !== null);

  return normalizedPaths.filter((path) => {
    if (seen.has(path)) return false;
    seen.add(path);
    return true;
  });
}

function normalizePathList(paths: string[] | undefined): string[] {
  const seen = new Set<string>();
  return (paths ?? [])
    .map((path) => normalizeOptionalPath(path))
    .filter((path): path is string => path !== null)
    .filter((path) => {
      if (seen.has(path)) return false;
      seen.add(path);
      return true;
    });
}

function normalizePath(path: string): string {
  return path.trim().replace(/\/+$/, '');
}

function normalizeOptionalPath(path: string | null | undefined): string | null {
  if (typeof path !== 'string') return null;
  const normalizedPath = normalizePath(path);
  return normalizedPath.length === 0 ? null : normalizedPath;
}

function normalizeOptionalString(value: string | null | undefined): string | null {
  if (typeof value !== 'string') return null;
  const normalizedValue = value.trim();
  return normalizedValue.length === 0 ? null : normalizedValue;
}

function normalizeEmbeddedTerminal(
  terminal: WorkspaceSnapshotEmbeddedTerminal | null | undefined
): WorkspaceSnapshotEmbeddedTerminal | null {
  if (!terminal) return null;

  const sessionID = normalizeOptionalString(terminal.sessionID);
  const cwd = normalizeOptionalPath(terminal.cwd);
  if (!sessionID || !cwd || !isSafeEmbeddedTerminalSessionID(sessionID) || !isAbsolutePath(cwd)) {
    return null;
  }

  return {
    sessionID,
    cwd,
    shell: normalizeOptionalString(terminal.shell),
    startedAt: normalizeTimestamp(terminal.startedAt)
  };
}

function isSafeEmbeddedTerminalSessionID(sessionID: string): boolean {
  return /^[A-Za-z0-9:._-]+$/.test(sessionID);
}

function isAbsolutePath(path: string): boolean {
  return path.startsWith('/');
}

function normalizeLine(line: number | null | undefined): number | null {
  if (typeof line !== 'number' || !Number.isFinite(line) || line < 1) return null;
  return Math.round(line);
}

function normalizeTimestamp(timestamp: number | null | undefined): number | null {
  if (typeof timestamp !== 'number' || !Number.isFinite(timestamp) || timestamp < 0) return null;
  return Math.floor(timestamp);
}

function normalizeCapturedAt(capturedAt: number | null | undefined): number {
  if (typeof capturedAt !== 'number' || !Number.isFinite(capturedAt)) return Date.now();
  return Math.max(0, Math.floor(capturedAt));
}
