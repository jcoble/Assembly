import assert from 'node:assert/strict';
import test from 'node:test';
import { createWorkspaceSessionSnapshotPlan, workspaceSnapshotProviderForSession } from '../src/lib/workspaceSnapshotPlan.ts';
import { createDefaultSourceDockLayout } from '../src/lib/sourceDockLayout.ts';
import {
  createWorkspaceSnapshot,
  describeWorkspaceSnapshotRestoreReadiness,
  parseStoredWorkspaceSnapshot,
  restoreWorkspaceSnapshot,
  selectStartupWorkspaceSnapshot,
  snapshotStorageKey,
  upsertWorkspaceSnapshot,
  workspaceSnapshotsForWorktreePath
} from '../src/lib/workspaceSnapshot.ts';

const dockLayout = createDefaultSourceDockLayout();

const snapshot = createWorkspaceSnapshot({
  provider: 'codex',
  sessionID: '019c-session',
  title: 'Review checkout flow',
  model: 'gpt-5.5 xhigh',
  project: {
    id: 'ediplatform',
    name: 'EdiPlatform',
    path: '/Users/blackcolours/dev/work/EdiPlatform/'
  },
  cwd: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/',
  worktreePath: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/',
  branch: 'cdx/tsk-127-command-center',
  selectedPath: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/App.ts',
  selectedLine: 42.7,
  openPaths: [
    '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/App.ts',
    '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/App.ts',
    '   ',
    '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/routes/+page.svelte'
  ],
  sourceActivityMode: 'conversations',
  sourceTerminalApp: 'Warp',
  browserUrl: '  http://localhost:5177/customer/dashboard  ',
  viewState: {
    contextPanelMode: 'stack',
    contextPanelPlacement: 'side',
    contextPanelCollapsed: true,
    editorInsightCollapsed: false,
    sidePanePosition: 'right',
    sidePaneWidth: 612,
    contextPaneWidth: 452,
    contextPaneHeight: 318,
    editorInsightWidth: 284,
    sourceChromeCompact: true,
    sourceActivityFilter: 'tsk-127',
    hiddenContextCardIDs: ['runtime', 'runtime', 'repo'],
    activeContextCardID: 'agents',
    sourceIntelligencePanel: 'git'
  },
  dockLayout,
  embeddedTerminal: {
    sessionID: 'terminal-123',
    cwd: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center',
    shell: '/bin/zsh',
    startedAt: 1_111
  },
  resumeCommand: 'cd /Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center && codex resume 019c-session',
  capturedAt: 1_000
});

assert.equal(snapshotStorageKey, 'mac-command-bar.workspace-snapshots');
assert.equal(snapshot.id, 'codex:019c-session');
assert.equal(snapshot.title, 'Review checkout flow');
assert.equal(snapshot.project.path, '/Users/blackcolours/dev/work/EdiPlatform');
assert.equal(snapshot.cwd, '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center');
assert.equal(snapshot.worktreePath, '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center');
assert.equal(snapshot.selectedLine, 43);
assert.deepEqual(snapshot.embeddedTerminal, {
  sessionID: 'terminal-123',
  cwd: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center',
  shell: '/bin/zsh',
  startedAt: 1_111
});
assert.deepEqual(snapshot.openPaths, [
  '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/App.ts',
  '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/routes/+page.svelte'
]);
assert.deepEqual(snapshot.viewState, {
  contextPanelMode: 'stack',
  contextPanelPlacement: 'side',
  contextPanelCollapsed: true,
  editorInsightCollapsed: false,
  sidePanePosition: 'right',
  sidePaneWidth: 612,
  contextPaneWidth: 452,
  contextPaneHeight: 318,
  editorInsightWidth: 284,
  sourceChromeCompact: true,
  sourceActivityFilter: 'tsk-127',
  hiddenContextCardIDs: ['runtime', 'repo'],
  activeContextCardID: 'agents',
  sourceIntelligencePanel: 'git'
});
assert.equal(snapshot.dockLayout.activePanelByGroup.right, 'context');

const narrowInsightSnapshot = createWorkspaceSnapshot({
  provider: 'manual',
  sessionID: 'narrow-insight',
  title: 'Narrow insight pane',
  project: { id: 'mac-command-bar', name: 'MacCommandBar', path: '/repo' },
  cwd: '/repo',
  viewState: {
    editorInsightWidth: 104
  },
  capturedAt: 1_100
});
assert.equal(
  narrowInsightSnapshot.viewState.editorInsightWidth,
  104,
  'workspace snapshots should preserve valid narrow editor insight pane widths'
);

const restored = restoreWorkspaceSnapshot(snapshot);
assert.deepEqual(restored.selectedProjectID, 'ediplatform');
assert.deepEqual(restored.selectedSourcePaths, {
  ediplatform: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/App.ts'
});
assert.equal(restored.selectedPath, '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/src/App.ts');
assert.equal(restored.selectedLine, 43);
assert.equal(restored.sourceActivityMode, 'conversations');
assert.equal(restored.sourceTerminalApp, 'Warp');
assert.equal(restored.browserUrl, 'http://localhost:5177/customer/dashboard');
assert.deepEqual(restored.viewState, snapshot.viewState);
assert.equal(
  restored.viewState.sidePaneWidth,
  612,
  'workspace restore should preserve the saved activity pane width'
);
assert.equal(
  restored.viewState.contextPaneWidth,
  452,
  'workspace restore should preserve the saved side context pane width'
);
assert.equal(
  restored.viewState.contextPaneHeight,
  318,
  'workspace restore should preserve the saved bottom context pane height'
);
assert.equal(
  restored.viewState.editorInsightWidth,
  284,
  'workspace restore should preserve the saved editor insight width'
);
assert.equal(restored.cwd, '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center');
assert.deepEqual(restored.embeddedTerminal, snapshot.embeddedTerminal);
assert.equal(restored.dockLayout.activePanelByGroup.right, 'context');

const unsafeBrowserSnapshot = createWorkspaceSnapshot({
  provider: 'manual',
  sessionID: 'unsafe-browser-url',
  title: 'Unsafe browser URL',
  project: { id: 'mac-command-bar', name: 'MacCommandBar', path: '/repo' },
  cwd: '/repo',
  browserUrl: ' javascript:alert(1) ',
  capturedAt: 1_250
});

assert.equal(
  unsafeBrowserSnapshot.browserUrl,
  null,
  'workspace snapshots should drop executable browser dock URLs'
);

assert.equal(
  restoreWorkspaceSnapshot({ ...unsafeBrowserSnapshot, browserUrl: 'file:///etc/passwd' }).browserUrl,
  'file:///etc/passwd',
  'workspace restore should preserve explicit local file browser URLs'
);
assert.equal(
  createWorkspaceSnapshot({
    provider: 'manual',
    sessionID: 'credentialed-browser-url',
    title: 'Credentialed browser URL',
    project: { id: 'mac-command-bar', name: 'MacCommandBar', path: '/repo' },
    cwd: '/repo',
    browserUrl: ' https://user:secret@example.com/private ',
    capturedAt: 1_275
  }).browserUrl,
  null,
  'workspace snapshots should drop browser URLs that embed credentials'
);

const unsafeTerminalSnapshot = createWorkspaceSnapshot({
  provider: 'manual',
  sessionID: 'unsafe-terminal',
  title: 'Unsafe terminal metadata',
  project: { id: 'mac-command-bar', name: 'MacCommandBar', path: '/repo' },
  cwd: '/repo',
  embeddedTerminal: {
    sessionID: 'terminal\n123',
    cwd: 'relative/worktree',
    shell: '/bin/zsh',
    startedAt: 1_300
  },
  capturedAt: 1_300
});
assert.equal(
  unsafeTerminalSnapshot.embeddedTerminal,
  null,
  'workspace snapshots should drop unsafe embedded terminal metadata'
);

const olderSnapshot = createWorkspaceSnapshot({
  provider: 'claude',
  sessionID: 'claude-session',
  title: 'Older task',
  project: { id: 'mac-command-bar', name: 'MacCommandBar', path: '/repo' },
  cwd: '/repo',
  capturedAt: 500
});

const selectedProjectSnapshot = createWorkspaceSnapshot({
  provider: 'manual',
  sessionID: 'ediplatform',
  title: 'Latest selected project snapshot',
  project: {
    id: 'ediplatform',
    name: 'EdiPlatform',
    path: '/Users/blackcolours/dev/work/EdiPlatform'
  },
  cwd: '/Users/blackcolours/dev/work/EdiPlatform',
  selectedPath: '/Users/blackcolours/dev/work/EdiPlatform/EdiPlatform.Core/Services/FormatResolver.cs',
  capturedAt: 1_500
});

const nestedWorktreeSnapshot = createWorkspaceSnapshot({
  provider: 'claude',
  sessionID: 'nested-worktree',
  title: 'Nested web task',
  project: {
    id: 'ediplatform',
    name: 'EdiPlatform',
    path: '/Users/blackcolours/dev/work/EdiPlatform'
  },
  cwd: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/ediplatform-web',
  worktreePath: null,
  branch: 'cdx/tsk-127-command-center',
  selectedPath: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/ediplatform-web/src/routes/+page.svelte',
  capturedAt: 2_500
});

const updatedSnapshot = createWorkspaceSnapshot({
  ...snapshot,
  title: 'Review checkout flow resumed',
  selectedLine: 9,
  capturedAt: 2_000
});

assert.deepEqual(
  upsertWorkspaceSnapshot([olderSnapshot, snapshot], updatedSnapshot, 8).map((item) => [
    item.id,
    item.title,
    item.selectedLine
  ]),
  [
    ['codex:019c-session', 'Review checkout flow resumed', 9],
    ['claude:claude-session', 'Older task', null]
  ],
  'upserting a snapshot should replace the same provider/session and keep newest first'
);

assert.equal(
  upsertWorkspaceSnapshot([updatedSnapshot, olderSnapshot], olderSnapshot, 1).length,
  1,
  'snapshot history should be bounded'
);

assert.deepEqual(
  workspaceSnapshotsForWorktreePath(
    [olderSnapshot, snapshot, selectedProjectSnapshot, nestedWorktreeSnapshot],
    '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/',
    8
  ).map((item) => [item.id, item.title, item.capturedAt]),
  [
    ['claude:nested-worktree', 'Nested web task', 2_500],
    ['codex:019c-session', 'Review checkout flow', 1_000]
  ],
  'worktree snapshot lookup should match saved worktree roots and nested cwd sessions newest first'
);

assert.deepEqual(
  workspaceSnapshotsForWorktreePath([snapshot, nestedWorktreeSnapshot], '', 8),
  [],
  'worktree snapshot lookup should ignore empty worktree paths'
);

assert.equal(
  workspaceSnapshotsForWorktreePath([snapshot, nestedWorktreeSnapshot], snapshot.worktreePath ?? '', 1).length,
  1,
  'worktree snapshot lookup should honor the requested display limit'
);

assert.equal(
  selectStartupWorkspaceSnapshot([olderSnapshot, selectedProjectSnapshot, snapshot], {
    activeSessionKey: snapshot.id,
    selectedProjectID: 'ediplatform',
    selectedProjectPath: '/Users/blackcolours/dev/work/EdiPlatform'
  })?.id,
  snapshot.id,
  'startup restore should prefer the active conversation snapshot'
);

assert.equal(
  selectStartupWorkspaceSnapshot([olderSnapshot, selectedProjectSnapshot, snapshot], {
    selectedProjectID: 'ediplatform',
    selectedProjectPath: '/Users/blackcolours/dev/work/EdiPlatform'
  })?.id,
  selectedProjectSnapshot.id,
  'startup restore should fall back to the newest snapshot for the stored project'
);

assert.equal(
  selectStartupWorkspaceSnapshot([olderSnapshot, selectedProjectSnapshot], {
    activeSessionKey: 'missing-session',
    selectedProjectID: 'missing-project',
    selectedProjectPath: '/missing/project'
  }),
  null,
  'startup restore should ignore stale active keys when no stored project snapshot matches'
);

const storedSnapshot = parseStoredWorkspaceSnapshot({
  ...snapshot,
  viewState: {
    contextPanelMode: 'stack',
    contextPanelPlacement: 'bottom',
    contextPanelCollapsed: false,
    editorInsightCollapsed: false,
    sidePanePosition: 'right',
    sidePaneWidth: 12,
    contextPaneWidth: 9999,
    contextPaneHeight: 25,
    editorInsightWidth: Number.NaN,
    sourceChromeCompact: false,
    sourceActivityFilter: '  task trail  ',
    hiddenContextCardIDs: ['runtime', 'bad-card', 'repo'],
    activeContextCardID: 'worktrees',
    sourceIntelligencePanel: 'symbols'
  },
  embeddedTerminal: {
    sessionID: 'terminal-456',
    cwd: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center',
    shell: '/bin/zsh',
    startedAt: 2_222
  },
  browserUrl: ' http://localhost:6190/customer/trading-partners '
});

assert.deepEqual(
  storedSnapshot?.viewState,
  {
    contextPanelMode: 'stack',
    contextPanelPlacement: 'bottom',
    contextPanelCollapsed: false,
    editorInsightCollapsed: false,
    sidePanePosition: 'right',
    sidePaneWidth: 40,
    contextPaneWidth: 1600,
    contextPaneHeight: 96,
    editorInsightWidth: 260,
    sourceChromeCompact: false,
    sourceActivityFilter: 'task trail',
    hiddenContextCardIDs: ['runtime', 'repo'],
    activeContextCardID: 'worktrees',
    sourceIntelligencePanel: 'git'
  },
  'stored snapshots should preserve normalized workspace view state (legacy intelligence panel coerces to git)'
);
assert.deepEqual(
  storedSnapshot?.embeddedTerminal,
  {
    sessionID: 'terminal-456',
    cwd: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center',
    shell: '/bin/zsh',
    startedAt: 2_222
  },
  'stored snapshots should preserve embedded terminal restore metadata'
);
assert.equal(
  storedSnapshot?.browserUrl,
  'http://localhost:6190/customer/trading-partners',
  'stored snapshots should preserve normalized browser dock URLs'
);
assert.equal(parseStoredWorkspaceSnapshot({ ...snapshot, provider: 'unknown' }), null);

assert.deepEqual(
  describeWorkspaceSnapshotRestoreReadiness(snapshot, {
    liveTerminalSessionIDs: ['terminal-123'],
    liveTerminalCwds: [],
    knownWorktreePaths: []
  }),
  {
    kind: 'live-terminal',
    tone: 'ready',
    label: 'Live terminal',
    detail: 'Can reattach to the saved embedded terminal session.',
    canRestoreWorkspace: true,
    canResumeEmbedded: true
  },
  'restore readiness should prefer a live saved terminal session'
);

assert.deepEqual(
  describeWorkspaceSnapshotRestoreReadiness(snapshot, {
    liveTerminalSessionIDs: [],
    liveTerminalCwds: [],
    knownWorktreePaths: ['/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center']
  }),
  {
    kind: 'ready',
    tone: 'ready',
    label: 'Worktree ready',
    detail: 'Saved worktree is still registered and can be restored.',
    canRestoreWorkspace: true,
    canResumeEmbedded: true
  },
  'restore readiness should recognize a still-registered saved worktree'
);

assert.deepEqual(
  describeWorkspaceSnapshotRestoreReadiness(snapshot, {
    knownWorktreePaths: ['/Users/blackcolours/dev/work/worktrees/EdiPlatform/other-task']
  }),
  {
    kind: 'missing-worktree',
    tone: 'blocked',
    label: 'Worktree missing',
    detail: 'Saved worktree is not in the current worktree scan; restore files cautiously.',
    repairLabel: 'Copy repair plan',
    repairDetail: 'Audit Git worktree metadata, prune stale registrations, or recreate the saved worktree before resuming terminal commands.',
    canRestoreWorkspace: true,
    canResumeEmbedded: false
  },
  'restore readiness should warn when a saved worktree is no longer registered'
);

const commandOnlySnapshot = createWorkspaceSnapshot({
  provider: 'codex',
  sessionID: 'command-only',
  title: 'Command-only resume',
  project: { id: 'mac-command-bar', name: 'MacCommandBar', path: '/repo' },
  cwd: '/repo',
  resumeCommand: 'codex resume command-only',
  capturedAt: 3_000
});

assert.deepEqual(
  describeWorkspaceSnapshotRestoreReadiness(commandOnlySnapshot),
  {
    kind: 'needs-terminal',
    tone: 'warning',
    label: 'Needs terminal',
    detail: 'Restore can start a new embedded terminal and run the saved command.',
    canRestoreWorkspace: true,
    canResumeEmbedded: true
  },
  'restore readiness should flag command snapshots that need a fresh terminal'
);

const filesOnlySnapshot = createWorkspaceSnapshot({
  provider: 'manual',
  sessionID: 'files-only',
  title: 'Files only',
  project: { id: 'mac-command-bar', name: 'MacCommandBar', path: '/repo' },
  cwd: '/repo',
  capturedAt: 4_000
});

assert.deepEqual(
  describeWorkspaceSnapshotRestoreReadiness(filesOnlySnapshot),
  {
    kind: 'files-only',
    tone: 'neutral',
    label: 'Files only',
    detail: 'Restores panes, selected file, and open tabs; no resume command was saved.',
    canRestoreWorkspace: true,
    canResumeEmbedded: false
  },
  'restore readiness should describe snapshots without a resume command'
);

for (const provider of ['antigravity', 'gemini'] as const) {
  test(provider + ' snapshot keeps its provider through planning and storage', () => {
    const plan = createWorkspaceSessionSnapshotPlan({
      selectedProject: snapshot.project,
      session: { provider, id: 'provider-session', title: 'Provider snapshot', model: null,
        projectPath: snapshot.cwd, resumeCommands: [] }
    });
    assert.equal(plan.snapshot.provider, provider);
    const restored = parseStoredWorkspaceSnapshot(JSON.parse(JSON.stringify(plan.snapshot)));
    assert.equal(restored?.provider, provider);
    assert.equal(restored?.id, provider + ':provider-session');
  });
}

test('snapshot ownership accepts known providers and normalizes aliases', () => {
  for (const [provider, expected] of Object.entries({
    codex: 'codex', claude: 'claude', antigravity: 'antigravity', gemini: 'gemini',
    opencode: 'opencode', agy: 'antigravity', anthropic: 'claude', openai: 'codex',
    'cmux-gemini': 'cmux', cmux: 'cmux', unknown: 'manual'
  })) {
    assert.equal(workspaceSnapshotProviderForSession({ provider, id: 'session', title: 'Snapshot',
      model: null, projectPath: null, resumeCommands: [] }), expected);
  }
});

test('snapshot validation accepts every known provider plus cmux and manual', () => {
  for (const provider of ['codex', 'claude', 'antigravity', 'gemini', 'opencode', 'cmux', 'manual'] as const) {
    const stored = createWorkspaceSnapshot({ ...snapshot, provider });
    assert.equal(parseStoredWorkspaceSnapshot(JSON.parse(JSON.stringify(stored)))?.provider, provider);
  }
  assert.equal(parseStoredWorkspaceSnapshot({ ...snapshot, provider: 'unknown' }), null);
});
