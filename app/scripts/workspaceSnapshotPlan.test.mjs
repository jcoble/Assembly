import assert from 'node:assert/strict';
import {
  activateSourceDockPanel,
  createDefaultSourceDockLayout,
  showSourceDockPanel
} from '../src/lib/sourceDockLayout.ts';
import {
  createWorkspaceSessionSnapshotPlan,
  workspaceSnapshotProviderForSession,
  workspaceSnapshotSessionIDForSession
} from '../src/lib/workspaceSnapshotPlan.ts';

const mainProject = {
  id: 'mac-command-bar',
  name: 'MacCommandBar',
  path: '/Users/blackcolours/dev/work/mac-command-bar'
};
const worktreePath = '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-44-plan';
const selectedRelativePath = 'tauri-svelte-preview/src/routes/+page.svelte';
const libRelativePath = 'tauri-svelte-preview/src/lib/workspaceSnapshot.ts';
const expectedSelectedPath = `${worktreePath}/${selectedRelativePath}`;
const expectedLibPath = `${worktreePath}/${libRelativePath}`;
const dockLayout = activateSourceDockPanel(
  showSourceDockPanel(showSourceDockPanel(createDefaultSourceDockLayout(), 'terminal'), 'browser'),
  'browser'
);
const session = {
  provider: 'codex',
  id: '019c-session',
  title: 'Add snapshot planning',
  model: 'gpt-5',
  projectPath: `${worktreePath}/`,
  lastActivity: '2026-06-15T12:00:00.000Z',
  resumeCommands: [
    `cd ${worktreePath} && codex resume 019c-session`,
    'codex resume 019c-session'
  ]
};

const plan = createWorkspaceSessionSnapshotPlan({
  selectedProject: mainProject,
  projectOptions: [mainProject],
  session,
  selectedRecord: {
    path: `${mainProject.path}/${selectedRelativePath}`,
    relativePath: selectedRelativePath
  },
  selectedSourcePaths: {},
  openSourceTabs: [
    {
      projectID: 'mac-command-bar',
      path: `${mainProject.path}/${libRelativePath}`,
      relativePath: libRelativePath
    },
    {
      projectID: 'other-project',
      path: '/tmp/other.ts',
      relativePath: 'other.ts'
    }
  ],
  selectedLine: 22.7,
  sourceActivityMode: 'sessions',
  sourceTerminalApp: 'Ghostty',
  browserUrl: 'localhost:5177/source',
  viewState: {
    contextPanelMode: 'stack',
    contextPanelPlacement: 'side',
    contextPanelCollapsed: false,
    editorInsightCollapsed: false,
    sidePanePosition: 'left',
    sidePaneWidth: 420,
    contextPaneWidth: 360,
    contextPaneHeight: 280,
    editorInsightWidth: 300,
    sourceChromeCompact: true,
    sourceActivityFilter: 'tsk-44',
    hiddenContextCardIDs: ['runtime'],
    activeContextCardID: 'agents',
    sourceIntelligencePanel: 'git'
  },
  embeddedTerminal: {
    sessionID: 'terminal-1',
    cwd: worktreePath,
    shell: '/bin/zsh',
    startedAt: 2_000
  },
  dockLayout,
  branch: 'cdx/tsk-44-plan',
  capturedAt: 5_000
});

assert.equal(plan.snapshot.id, 'codex:019c-session');
assert.equal(plan.snapshot.project.name, 'MacCommandBar');
assert.equal(plan.snapshot.project.path, worktreePath);
assert.equal(plan.rootPath, worktreePath);
assert.equal(plan.worktreePath, worktreePath);
assert.equal(plan.snapshot.cwd, worktreePath);
assert.equal(plan.snapshot.worktreePath, worktreePath);
assert.equal(plan.snapshot.selectedPath, expectedSelectedPath);
assert.equal(plan.snapshot.selectedLine, 23);
assert.deepEqual(plan.snapshot.openPaths, [expectedSelectedPath, expectedLibPath]);
assert.equal(plan.snapshot.resumeCommand, 'codex resume 019c-session');
assert.equal(plan.snapshot.browserUrl, 'http://localhost:5177/source');
assert.equal(plan.panelSelection.activePanelByGroup.center, 'browser');
assert.equal(plan.panelSelection.activePanelByGroup.bottom, undefined);
assert.deepEqual(plan.panelSelection.visiblePanelIDs, [
  'activity',
  'editor',
  'terminal',
  'browser',
  'context',
  'insights'
]);
assert.equal(plan.panelSelection.activeContextCardID, 'agents');
assert.equal(plan.panelSelection.sourceIntelligencePanel, 'git');
assert.deepEqual(plan.terminalContext, {
  app: 'Ghostty',
  embeddedSessionID: 'terminal-1',
  cwd: worktreePath,
  hasResumeCommand: true
});
assert.deepEqual(plan.browserContext, {
  url: 'http://localhost:5177/source'
});
assert.ok(
  plan.summaryLines.includes(`Root: ${worktreePath}`),
  'snapshot plan should summarize the root that will be restored'
);
assert.ok(
  plan.summaryLines.includes('Open tabs: 2'),
  'snapshot plan should summarize remembered tab count'
);
assert.ok(
  plan.summaryLines.includes('Selected panels: Activity, Browser, Context, Insights'),
  'snapshot plan should summarize active selected panels'
);

const manualPlan = createWorkspaceSessionSnapshotPlan({
  selectedProject: mainProject,
  selectedSourcePaths: {
    'mac-command-bar': `${mainProject.path}/README.md`
  },
  openSourceTabs: [],
  sourceActivityMode: 'files',
  sourceTerminalApp: 'Warp',
  browserUrl: '',
  dockLayout: createDefaultSourceDockLayout(),
  capturedAt: 6_000
});

assert.equal(manualPlan.snapshot.id, 'manual:mac-command-bar');
assert.equal(manualPlan.snapshot.title, 'MacCommandBar workspace');
assert.equal(manualPlan.snapshot.project.path, mainProject.path);
assert.equal(manualPlan.snapshot.worktreePath, null);
assert.equal(manualPlan.snapshot.selectedPath, `${mainProject.path}/README.md`);
assert.deepEqual(manualPlan.snapshot.openPaths, [`${mainProject.path}/README.md`]);
assert.equal(manualPlan.snapshot.resumeCommand, null);
assert.equal(manualPlan.browserContext.url, null);
assert.deepEqual(manualPlan.terminalContext, {
  app: 'Warp',
  embeddedSessionID: null,
  cwd: null,
  hasResumeCommand: false
});

assert.equal(
  workspaceSnapshotProviderForSession({ ...session, provider: 'cmux-opus' }),
  'cmux',
  'cmux-prefixed sessions should use the cmux snapshot provider'
);
assert.equal(
  workspaceSnapshotSessionIDForSession({ ...session, provider: 'cmux-opus', id: 'abc' }),
  'cmux-opus:abc',
  'cmux-prefixed sessions should retain provider flavor in their session id'
);
assert.equal(
  workspaceSnapshotProviderForSession({ ...session, provider: 'unknown' }),
  'manual',
  'unknown session providers should fall back to manual snapshots'
);
