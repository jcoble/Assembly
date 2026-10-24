import { normalizeProvider } from './shell/ownedSessions.ts';
import {
  createProjectRoot,
  normalizeProjectPath,
  type ProjectRoot,
  type SourceRecord
} from './sourceData.ts';
import {
  normalizeSourceDockLayout,
  sourceDockPanelDescriptors,
  visibleSourceDockPanelIDs,
  type SourceDockGroupID,
  type SourceDockLayout,
  type SourceDockPanelID
} from './sourceDockLayout.ts';
import {
  createWorkspaceSnapshot,
  type WorkspaceSnapshot,
  type WorkspaceSnapshotActivityMode,
  type WorkspaceSnapshotContextCardID,
  type WorkspaceSnapshotEmbeddedTerminal,
  type WorkspaceSnapshotInput,
  type WorkspaceSnapshotIntelligencePanel,
  type WorkspaceSnapshotProvider,
  type WorkspaceSnapshotTerminalApp,
  type WorkspaceSnapshotViewState
} from './workspaceSnapshot.ts';

export type WorkspaceSnapshotPlanningSession = {
  provider: string;
  id: string;
  title: string;
  model: string | null;
  projectPath: string | null;
  resumeCommands: string[];
};

export type WorkspaceSnapshotPlanningSourceRecord = Pick<SourceRecord, 'path' | 'relativePath'> & {
  projectID?: string | null;
};

export type WorkspaceSessionSnapshotPlanInput = {
  selectedProject: ProjectRoot;
  projectOptions?: ProjectRoot[];
  session?: WorkspaceSnapshotPlanningSession | null;
  selectedRecord?: WorkspaceSnapshotPlanningSourceRecord | null;
  selectedSourcePaths?: Record<string, string>;
  openSourceTabs?: WorkspaceSnapshotPlanningSourceRecord[];
  selectedLine?: number | null;
  sourceActivityMode?: WorkspaceSnapshotActivityMode;
  sourceTerminalApp?: WorkspaceSnapshotTerminalApp;
  browserUrl?: string | null;
  viewState?: Partial<WorkspaceSnapshotViewState> | null;
  embeddedTerminal?: WorkspaceSnapshotEmbeddedTerminal | null;
  dockLayout?: SourceDockLayout;
  branch?: string | null;
  capturedAt?: number;
};

export type WorkspaceSnapshotPanelSelection = {
  activePanelByGroup: Partial<Record<SourceDockGroupID, SourceDockPanelID>>;
  visiblePanelIDs: SourceDockPanelID[];
  hiddenPanelIDs: SourceDockPanelID[];
  selectedPanelIDs: SourceDockPanelID[];
  activeContextCardID: WorkspaceSnapshotContextCardID;
  sourceIntelligencePanel: WorkspaceSnapshotIntelligencePanel;
};

export type WorkspaceSnapshotTerminalContext = {
  app: WorkspaceSnapshotTerminalApp;
  embeddedSessionID: string | null;
  cwd: string | null;
  hasResumeCommand: boolean;
};

export type WorkspaceSnapshotBrowserContext = {
  url: string | null;
};

export type WorkspaceSessionSnapshotPlan = {
  snapshot: WorkspaceSnapshot;
  rootPath: string;
  worktreePath: string | null;
  selectedPath: string | null;
  openPaths: string[];
  panelSelection: WorkspaceSnapshotPanelSelection;
  terminalContext: WorkspaceSnapshotTerminalContext;
  browserContext: WorkspaceSnapshotBrowserContext;
  summaryLines: string[];
};

export function createWorkspaceSessionSnapshotPlan(
  input: WorkspaceSessionSnapshotPlanInput
): WorkspaceSessionSnapshotPlan {
  const session = input.session ?? null;
  const selectedProject = normalizeProjectRoot(input.selectedProject);
  const projectOptions = (input.projectOptions ?? []).map(normalizeProjectRoot);
  const project = workspaceSnapshotProjectForSession(session, selectedProject, projectOptions);
  const cwd = normalizeProjectPath(session?.projectPath?.trim() || project.path || selectedProject.path);
  const selectedPath = workspaceSnapshotSelectedPathForProject(
    project,
    input.selectedRecord ?? null,
    input.selectedSourcePaths ?? {},
    selectedProject
  );
  const openPaths = workspaceSnapshotOpenPathsForProject(
    project,
    input.openSourceTabs ?? [],
    selectedProject
  );
  const resumeCommand = session ? workspaceSnapshotResumeCommandForSession(session) : null;
  const snapshotInput: WorkspaceSnapshotInput = {
    provider: workspaceSnapshotProviderForSession(session),
    sessionID: session ? workspaceSnapshotSessionIDForSession(session) : selectedProject.id,
    title: session?.title ?? `${selectedProject.name} workspace`,
    model: session?.model ?? null,
    project,
    cwd,
    worktreePath: snapshotWorktreePathForPath(cwd),
    branch: input.branch ?? null,
    selectedPath,
    selectedLine: input.selectedLine ?? null,
    openPaths,
    sourceActivityMode: input.sourceActivityMode,
    sourceTerminalApp: input.sourceTerminalApp,
    browserUrl: input.browserUrl ?? null,
    viewState: input.viewState ?? null,
    embeddedTerminal: input.embeddedTerminal ?? null,
    dockLayout: input.dockLayout,
    resumeCommand,
    capturedAt: input.capturedAt
  };
  const snapshot = createWorkspaceSnapshot(snapshotInput);
  const panelSelection = workspaceSnapshotPanelSelection(snapshot);
  const rootPath = snapshot.worktreePath ?? snapshot.cwd;
  const terminalContext = workspaceSnapshotTerminalContext(snapshot);
  const browserContext = workspaceSnapshotBrowserContext(snapshot);

  return {
    snapshot,
    rootPath,
    worktreePath: snapshot.worktreePath,
    selectedPath: snapshot.selectedPath,
    openPaths: snapshot.openPaths,
    panelSelection,
    terminalContext,
    browserContext,
    summaryLines: workspaceSnapshotPlanSummaryLines(snapshot, panelSelection, terminalContext, browserContext)
  };
}

export function workspaceSnapshotProviderForSession(
  session: WorkspaceSnapshotPlanningSession | null | undefined
): WorkspaceSnapshotProvider {
  const provider = session?.provider.trim().toLowerCase() ?? '';
  const { agent, viaCmux } = normalizeProvider(provider);
  if (viaCmux || provider === 'cmux') return 'cmux';
  return agent === 'other' ? 'manual' : agent;
}

export function workspaceSnapshotSessionIDForSession(session: WorkspaceSnapshotPlanningSession): string {
  const sessionID = session.id.trim() || 'session';
  const provider = session.provider.trim().toLowerCase();
  return provider.startsWith('cmux-') ? `${provider}:${sessionID}` : sessionID;
}

export function workspaceSnapshotProjectForSession(
  session: WorkspaceSnapshotPlanningSession | null | undefined,
  selectedProject: ProjectRoot,
  projectOptions: ProjectRoot[] = []
): ProjectRoot {
  const sessionPath = session?.projectPath?.trim();
  const normalizedSelectedProject = normalizeProjectRoot(selectedProject);
  if (!sessionPath) return normalizedSelectedProject;

  const normalizedSessionPath = normalizeProjectPath(sessionPath);
  const existingProject = projectOptions.find(
    (project) => normalizeProjectPath(project.path) === normalizedSessionPath
  );
  if (existingProject) return normalizeProjectRoot(existingProject);

  if (normalizedSessionPath === normalizedSelectedProject.path) return normalizedSelectedProject;

  return createProjectRoot(normalizedSelectedProject.name, normalizedSessionPath);
}

export function workspaceSnapshotSelectedPathForProject(
  project: ProjectRoot,
  selectedRecord: WorkspaceSnapshotPlanningSourceRecord | null,
  selectedSourcePaths: Record<string, string> = {},
  sourceProject: ProjectRoot = project
): string | null {
  if (selectedRecord) return sourceRecordPathForSnapshotProject(project, selectedRecord, sourceProject);

  const storedPath = selectedSourcePaths[project.id] ?? selectedSourcePaths[sourceProject.id] ?? null;
  if (!storedPath) return null;

  return sourceRecordPathForSnapshotProject(
    project,
    { path: storedPath, relativePath: relativePathFromSourcePath(storedPath, sourceProject) ?? '' },
    sourceProject
  );
}

export function workspaceSnapshotOpenPathsForProject(
  project: ProjectRoot,
  openSourceTabs: WorkspaceSnapshotPlanningSourceRecord[] = [],
  sourceProject: ProjectRoot = project
): string[] {
  return openSourceTabs
    .filter((tab) => sourceRecordMatchesSnapshotProject(tab, project, sourceProject))
    .map((tab) => sourceRecordPathForSnapshotProject(project, tab, sourceProject));
}

export function snapshotWorktreePathForPath(path: string | null | undefined): string | null {
  if (typeof path !== 'string') return null;
  const normalizedPath = normalizeProjectPath(path);
  return normalizedPath.includes('/worktrees/') ? normalizedPath : null;
}

export function workspaceSnapshotResumeCommandForSession(
  session: WorkspaceSnapshotPlanningSession
): string | null {
  const commands = session.resumeCommands.map((command) => command.trim()).filter(Boolean);
  const command = commands.find((candidate) => !isLeadingShellCdCommand(candidate))
    ?? stripLeadingShellCdCommand(commands[0] ?? '');

  return command || null;
}

export function workspaceSnapshotPanelSelection(
  snapshot: WorkspaceSnapshot
): WorkspaceSnapshotPanelSelection {
  const layout = normalizeSourceDockLayout(snapshot.dockLayout);
  const visiblePanelIDs = visibleSourceDockPanelIDs(layout);
  const selectedPanelIDSet = new Set(Object.values(layout.activePanelByGroup).filter(isSourceDockPanelID));
  if (!snapshot.viewState.editorInsightCollapsed && visiblePanelIDs.includes('insights')) {
    selectedPanelIDSet.add('insights');
  }

  return {
    activePanelByGroup: layout.activePanelByGroup,
    visiblePanelIDs,
    hiddenPanelIDs: layout.hiddenPanelIDs,
    selectedPanelIDs: visiblePanelIDs.filter((panelID) => selectedPanelIDSet.has(panelID)),
    activeContextCardID: snapshot.viewState.activeContextCardID,
    sourceIntelligencePanel: snapshot.viewState.sourceIntelligencePanel
  };
}

export function workspaceSnapshotTerminalContext(
  snapshot: WorkspaceSnapshot
): WorkspaceSnapshotTerminalContext {
  return {
    app: snapshot.sourceTerminalApp,
    embeddedSessionID: snapshot.embeddedTerminal?.sessionID ?? null,
    cwd: snapshot.embeddedTerminal?.cwd ?? null,
    hasResumeCommand: Boolean(snapshot.resumeCommand)
  };
}

export function workspaceSnapshotBrowserContext(
  snapshot: WorkspaceSnapshot
): WorkspaceSnapshotBrowserContext {
  return {
    url: snapshot.browserUrl
  };
}

export function workspaceSnapshotPlanSummaryLines(
  snapshot: WorkspaceSnapshot,
  panelSelection: WorkspaceSnapshotPanelSelection = workspaceSnapshotPanelSelection(snapshot),
  terminalContext: WorkspaceSnapshotTerminalContext = workspaceSnapshotTerminalContext(snapshot),
  browserContext: WorkspaceSnapshotBrowserContext = workspaceSnapshotBrowserContext(snapshot)
): string[] {
  return [
    'Workspace snapshot plan',
    `Project: ${snapshot.project.name}`,
    `Root: ${snapshot.worktreePath ?? snapshot.cwd}`,
    `Worktree: ${snapshot.worktreePath ?? 'none'}`,
    `Selected file: ${snapshot.selectedPath ?? 'none'}`,
    `Open tabs: ${snapshot.openPaths.length}`,
    `Selected panels: ${formatPanelLabels(panelSelection.selectedPanelIDs) || 'none'}`,
    `Context card: ${panelSelection.activeContextCardID}`,
    `Insights panel: ${panelSelection.sourceIntelligencePanel}`,
    `Terminal: ${formatTerminalContext(terminalContext)}`,
    `Browser: ${browserContext.url ?? 'none'}`
  ];
}

function normalizeProjectRoot(project: ProjectRoot): ProjectRoot {
  return {
    id: project.id.trim(),
    name: project.name.trim() || 'Project',
    path: normalizeProjectPath(project.path)
  };
}

function sourceRecordPathForSnapshotProject(
  project: ProjectRoot,
  sourceRecord: WorkspaceSnapshotPlanningSourceRecord,
  sourceProject: ProjectRoot
): string {
  const normalizedPath = normalizeProjectPath(sourceRecord.path);
  const projectPath = normalizeProjectPath(project.path);
  if (pathIsInsideRoot(normalizedPath, projectPath)) return normalizedPath;

  const relativePath = normalizeRelativePath(sourceRecord.relativePath)
    ?? relativePathFromSourcePath(normalizedPath, sourceProject);
  return relativePath ? `${projectPath}/${relativePath}` : normalizedPath;
}

function sourceRecordMatchesSnapshotProject(
  sourceRecord: WorkspaceSnapshotPlanningSourceRecord,
  project: ProjectRoot,
  sourceProject: ProjectRoot
): boolean {
  const projectID = sourceRecord.projectID?.trim();
  if (projectID && projectID !== project.id && projectID !== sourceProject.id) return false;

  return true;
}

function relativePathFromSourcePath(path: string, sourceProject: ProjectRoot): string | null {
  const normalizedPath = normalizeProjectPath(path);
  const sourceProjectPath = normalizeProjectPath(sourceProject.path);
  if (!pathIsInsideRoot(normalizedPath, sourceProjectPath) || normalizedPath === sourceProjectPath) {
    return null;
  }

  return normalizeRelativePath(normalizedPath.slice(sourceProjectPath.length + 1));
}

function normalizeRelativePath(path: string | null | undefined): string | null {
  if (typeof path !== 'string') return null;
  const normalizedPath = path.trim().replace(/^\/+/, '').replace(/\/+$/, '');
  return normalizedPath.length > 0 ? normalizedPath : null;
}

function pathIsInsideRoot(path: string, root: string): boolean {
  return path === root || path.startsWith(`${root}/`);
}

function isLeadingShellCdCommand(command: string): boolean {
  return /^cd\s+/.test(command.trim()) && command.includes('&&');
}

function stripLeadingShellCdCommand(command: string): string {
  const trimmed = command.trim();
  if (!isLeadingShellCdCommand(trimmed)) return trimmed;

  const separatorIndex = trimmed.indexOf('&&');
  return separatorIndex >= 0 ? trimmed.slice(separatorIndex + 2).trim() : trimmed;
}

function isSourceDockPanelID(value: unknown): value is SourceDockPanelID {
  return typeof value === 'string' && sourceDockPanelDescriptors.some((panel) => panel.id === value);
}

function formatPanelLabels(panelIDs: SourceDockPanelID[]): string {
  return panelIDs.map((panelID) => sourceDockPanelLabel(panelID)).join(', ');
}

function sourceDockPanelLabel(panelID: SourceDockPanelID): string {
  return sourceDockPanelDescriptors.find((panel) => panel.id === panelID)?.label ?? panelID;
}

function formatTerminalContext(context: WorkspaceSnapshotTerminalContext): string {
  const embeddedLabel = context.embeddedSessionID ? context.embeddedSessionID : 'no embedded session';
  const resumeLabel = context.hasResumeCommand ? 'resume command' : 'no resume command';
  return `${context.app} (${embeddedLabel}, ${resumeLabel})`;
}
