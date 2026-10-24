import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import ts from 'typescript';

import {
  closeTopTab,
  normalizeTopTabs,
  openTopTab,
  parseTopTabKey,
  resolveActiveKey,
  topTabKey,
  topTabRow
} from '../src/lib/shell/layout/topTabsOps.ts';

const live = {
  editorPaths: ['/repo/a.ts', '/repo/b.ts', '/repo/new.ts'],
  browserTabIds: ['tab-1', 'tab-new']
};

test('the row keeps stored order, drops gone content, keeps singletons, appends new live tabs', () => {
  const row = topTabRow(
    ['browser:tab-1', 'diff', 'editor:/repo/gone.ts', 'editor:/repo/b.ts', 'browser:tab-gone', 'pull-requests', 'editor:/repo/a.ts'],
    live
  );
  assert.deepEqual(row, [
    'browser:tab-1',
    'diff',
    'editor:/repo/b.ts',
    'pull-requests',
    'editor:/repo/a.ts',
    'editor:/repo/new.ts',
    'browser:tab-new'
  ]);
});

test('the active key falls back to the last tab, or null when the row is empty', () => {
  const row = ['diff', 'editor:/repo/a.ts'];
  assert.equal(resolveActiveKey(row, 'diff'), 'diff');
  assert.equal(resolveActiveKey(row, 'git-history'), 'editor:/repo/a.ts');
  assert.equal(resolveActiveKey(row, null), 'editor:/repo/a.ts');
  assert.equal(resolveActiveKey([], 'diff'), null);
});

test('closing the active tab picks the right neighbour, then the left, then nothing', () => {
  const row = ['diff', 'git-history', 'pull-requests'];
  assert.deepEqual(closeTopTab(row, 'git-history', 'git-history'), {
    order: ['diff', 'pull-requests'],
    activeKey: 'pull-requests'
  });
  assert.deepEqual(closeTopTab(row, 'pull-requests', 'pull-requests'), {
    order: ['diff', 'git-history'],
    activeKey: 'git-history'
  });
  assert.deepEqual(closeTopTab(['diff'], 'diff', 'diff'), { order: [], activeKey: null });
});

test('closing an inactive tab keeps the active key', () => {
  assert.deepEqual(closeTopTab(['diff', 'git-history', 'pull-requests'], 'diff', 'pull-requests'), {
    order: ['diff', 'git-history'],
    activeKey: 'diff'
  });
});

test('opening a tab appends it once', () => {
  const once = openTopTab(['diff'], 'git-history');
  assert.deepEqual(once, ['diff', 'git-history']);
  assert.deepEqual(openTopTab(once, 'git-history'), ['diff', 'git-history']);
  assert.deepEqual(openTopTab(once, 'diff'), ['diff', 'git-history']);
});

test('a key round-trips for a path that contains a colon', () => {
  const ref = { kind: 'editor' as const, id: 'C:/repo/odd:name.ts' };
  const key = topTabKey(ref);
  assert.equal(key, 'editor:C:/repo/odd:name.ts');
  assert.deepEqual(parseTopTabKey(key), ref);
  assert.deepEqual(parseTopTabKey('browser:tab-1'), { kind: 'browser', id: 'tab-1' });
  assert.deepEqual(parseTopTabKey('diff'), { kind: 'diff', id: 'diff' });
  assert.equal(topTabKey({ kind: 'diff', id: 'diff' }), 'diff');
  assert.equal(parseTopTabKey('session'), null);
  assert.equal(parseTopTabKey('editor:'), null);
});

test('normalize drops junk, unknown kinds, duplicates, gone editors and browsers, and a stray active key', () => {
  const normalized = normalizeTopTabs(
    {
      order: [
        7,
        null,
        'terminal',
        'diff',
        'diff',
        'editor:/repo/a.ts',
        'editor:/repo/closed.ts',
        'browser:tab-1',
        'browser:tab-closed',
        'pull-requests'
      ],
      activeKey: 'editor:/repo/closed.ts'
    },
    { editorPaths: ['/repo/a.ts'], browserTabIds: ['tab-1'] }
  );
  assert.deepEqual(normalized, {
    order: ['diff', 'editor:/repo/a.ts', 'browser:tab-1', 'pull-requests'],
    activeKey: null
  });
  assert.deepEqual(
    normalizeTopTabs({ order: ['git-history'], activeKey: 'git-history' }, { editorPaths: [], browserTabIds: [] }),
    { order: ['git-history'], activeKey: 'git-history' }
  );
  assert.equal(normalizeTopTabs('nope', { editorPaths: [], browserTabIds: [] }), undefined);
  assert.equal(normalizeTopTabs([], { editorPaths: [], browserTabIds: [] }), undefined);
});


test('failed remote selection releases outgoing tabs and preserves its active file', async () => {
  // Execute production selection and tab logic with storage/renderer I/O replaced.
  const source = readFileSync(new URL('../src/lib/shell/controllers/sessionSelectionController.svelte.ts', import.meta.url), 'utf8');
  const parsed = ts.createSourceFile('selection.ts', source, ts.ScriptTarget.Latest, true);
  const controller = parsed.statements.find(ts.isClassDeclaration)!;
  const method = controller.members.find((member) => ts.isMethodDeclaration(member) && member.name.getText(parsed) === 'materializeSelection')!;
  const methodCode = stripTypeScriptTypes(method.getText(parsed).replace('private async materializeSelection', 'async function materializeSelection'));
  const pageSource = readFileSync(new URL('../src/routes/+page.svelte', import.meta.url), 'utf8').split('<script lang="ts">')[1].split('</script>')[0];
  const page = ts.createSourceFile('page.ts', pageSource, ts.ScriptTarget.Latest, true);
  let effect: ts.ArrowFunction | undefined;
  function visit(node: ts.Node): void {
    if (ts.isCallExpression(node) && node.expression.getText(page) === '$effect') {
      const callback = node.arguments[0];
      if (ts.isArrowFunction(callback) && callback.getText(page).includes('const ref = topTabs.activeKey')) effect = callback;
    }
    ts.forEachChild(node, visit);
  }
  visit(page);
  const readme = '/repo/README.md';
  const alpha = '/repo/src/alpha.ts';
  const editor: { openFiles: { path: string }[]; activePath: string | null } = { openFiles: [{ path: readme }, { path: alpha }], activePath: readme };
  const workbench = { switching: true };
  const tabs = { activeKey: `editor:${alpha}` };
  const applyActiveTab = new Function('topTabs', 'workbench', 'editorState', 'editorPanel', 'parseTopTabKey', 'untrack',
    `return ${stripTypeScriptTypes(effect!.getText(page))}`)(tabs, workbench, editor, { selectFile: (path: string) => { editor.activePath = path; } }, parseTopTabKey, (run: () => void) => run()) as () => void;
  applyActiveTab();
  assert.equal(editor.activePath, readme, 'resetting the top row must not select the outgoing last file mid-switch');

  const owner = { signal: new AbortController().signal };
  const remote = { ownedId: 'remote', executionEnvironment: 'remote', root: 'assembly-remote://offline/checkout' };
  const saved = new Map<string, { openPaths: string[]; activePath: string | null }>([['hello', { openPaths: [readme, alpha], activePath: readme }], ['remote', { openPaths: [], activePath: null }]]);
  let editorOwner = 'hello';
  const restores: unknown[][] = [];
  const restore = async (id: string, root: string, available: boolean, signal: AbortSignal) => {
    restores.push([id, root, available, signal]);
    saved.set(editorOwner, { openPaths: editor.openFiles.map((file) => file.path), activePath: editor.activePath });
    editorOwner = id;
    const snapshot = saved.get(id)!;
    editor.openFiles = available ? snapshot.openPaths.map((path) => ({ path })) : [];
    editor.activePath = available ? snapshot.activePath : null;
    return snapshot;
  };
  const state = {
    isCurrent: () => true, activeWorkspaceSnapshot: null,
    newSession: { abandonDraftForSessionSwitch: () => {} },
    sessionSelectionLayers: { selectSession: async () => { throw new Error('remote machine is not connected'); }, treeRoot: '/repo' },
    editorSessions: { restoreEditorWorkspaceForSession: restore, releaseOwnership: () => { released += 1; } }
  };
  let released = 0;
  const select = new Function('captureConversationWorkspace', 'shellPanels', 'rail', `${methodCode}; return materializeSelection;`)(
    () => null, { sessionPicked: () => {} }, { remoteConnections: {} }
  ) as (this: typeof state, session: typeof remote, root: string, owner: { signal: AbortSignal }, displayedId: string) => Promise<void>;
  await assert.rejects(select.call(state, remote, remote.root, owner, 'hello'), /remote machine is not connected/);
  assert.deepEqual(restores, [['remote', remote.root, false, owner.signal]]);
  assert.equal(released, 1, 'the failed selection does not own the editor it emptied');
  assert.deepEqual(topTabRow([], { editorPaths: editor.openFiles.map((file) => file.path), browserTabIds: [] }), []);
  assert.equal(state.activeWorkspaceSnapshot, saved.get('remote'));
  await restore('hello', '/repo', true, owner.signal);
  assert.equal(editor.activePath, readme, 'returning to hello retains README');
  saved.set('local', { openPaths: [], activePath: null });
  await restore('local', '/local', true, owner.signal);
  await restore('hello', '/repo', true, owner.signal);
  assert.equal(editor.activePath, readme, 'a local round trip also retains README');
  workbench.switching = false;
  applyActiveTab();
  assert.equal(editor.activePath, alpha, 'the active tab drives its owner after switching finishes');
  let restoreAttempts = 0;
  state.sessionSelectionLayers.selectSession = async () => {};
  const requiredFailure = new Error('required workspace checkpoint failed');
  state.editorSessions.restoreEditorWorkspaceForSession = async () => { restoreAttempts += 1; throw requiredFailure; };
  const outerMethod = controller.members.find((member) => ts.isMethodDeclaration(member) && member.name.getText(parsed) === 'selectSessionOnce')!;
  const outerCode = stripTypeScriptTypes(outerMethod.getText(parsed).replace('private async selectSessionOnce', 'async function selectSessionOnce'));
  const outerState = {
    ...state, beginSelection: () => owner, chatOwnedId: 'hello', materializeSelection: select.bind(state),
    sessionSelectionLayers: { ...state.sessionSelectionLayers, clearTreeView: () => {}, clearChatHistory: () => {} }
  };
  const selectOnce = new Function('rail', 'canonicalPath', 'sessionWorkspaceRoot', 'projectRegistry', 'shellPanels', `${outerCode}; return selectSessionOnce;`)(
    { owned: [remote] }, (path: string) => path, (session: typeof remote) => session.root, { projects: [] }, { sessionPicked: () => {} }
  ) as (this: typeof outerState, id: string) => Promise<void>;
  await selectOnce.call(outerState, 'remote');
  assert.equal(restoreAttempts, 1, 'a required editor save/read failure must not trigger another restore attempt');
});


test('an unreadable remote workspace releases editor ownership before returning to hello', async () => {
  const source = readFileSync(new URL('../src/lib/shell/controllers/editorSessionController.svelte.ts', import.meta.url), 'utf8');
  const parsed = ts.createSourceFile('editor.ts', source, ts.ScriptTarget.Latest, true);
  let code = source;
  for (const statement of [...parsed.statements].reverse()) {
    if (ts.isImportDeclaration(statement)) code = code.slice(0, statement.getStart(parsed)) + code.slice(statement.end);
  }
  code = stripTypeScriptTypes(code.replace('export class EditorSessionController', 'class EditorSessionController'));
  const readme = '/repo/README.md';
  const alpha = '/repo/src/alpha.ts';
  const editor: { openFiles: { path: string }[]; activePath: string | null } = { openFiles: [], activePath: null };
  const saved = new Map<string, { openPaths: string[]; activePath: string | null }>([['hello', { openPaths: [readme, alpha], activePath: readme }]]);
  const writes: string[] = [];
  const dependencies = {
    rail: { owned: [{ ownedId: 'remote', executionEnvironment: 'remote', remoteProfileId: 'environment' }], remoteConnections: { environment: 'connected' } },
    parseRemoteWorkspacePath: (root: string) => root.startsWith('assembly-remote:') ? { profileId: 'environment' } : null,
    mapWorkspaceSnapshotPaths: (snapshot: unknown) => snapshot,
    editorState: editor,
    resetEditorState: () => { editor.openFiles = []; editor.activePath = null; },
    restoreEditorFiles: (files: { path: string }[], active: string | null) => { editor.openFiles = files; editor.activePath = active; },
    setEditorProjectRoot: () => {},
    captureWorkspace: (value: typeof editor) => ({ openPaths: value.openFiles.map((file) => file.path), activePath: value.activePath }),
    planWorkspaceRestore: (snapshot: { openPaths: string[]; activePath: string | null }) => ({ openFiles: snapshot.openPaths.map((path) => ({ path })), activePath: snapshot.activePath }),
    captureConversationWorkspace: () => null,
    readAgentConversationWorkspaceFromTauri: async (id: string) => {
      if (id === 'remote') throw { message: 'Remote machine environment is not connected' };
      return saved.get(id) ?? null;
    },
    writeAgentConversationWorkspaceFromTauri: async (id: string, snapshot: { openPaths: string[]; activePath: string | null }) => { writes.push(id); saved.set(id, snapshot); }
  };
  interface EditorController {
    restoreEditorWorkspaceForSession(id: string, root: string, available: boolean, signal: AbortSignal): Promise<unknown>;
    rememberWorkspaceState(patch: object): unknown;
    releaseOwnership(): void;
  }
  const Controller = new Function(...Object.keys(dependencies), `${code}; return EditorSessionController;`)(...Object.values(dependencies)) as new () => EditorController;
  const controller = new Controller();
  const signal = new AbortController().signal;
  await controller.restoreEditorWorkspaceForSession('hello', '/repo', true, signal);
  assert.equal(await controller.restoreEditorWorkspaceForSession('remote', 'assembly-remote://environment/checkout', false, signal), null);
  assert.deepEqual(editor.openFiles, []);
  assert.equal(controller.rememberWorkspaceState({ topTabs: { order: [], activeKey: null } }), null, 'an unavailable incoming workspace has no editor owner to mutate');
  await controller.restoreEditorWorkspaceForSession('hello', '/repo', true, signal);
  assert.equal(editor.activePath, readme);
  assert.deepEqual(writes, ['hello'], 'returning must not checkpoint the remote or overwrite hello from empty editor state');

  // A projection can fail while the saved workspace is still readable. The
  // failed selection shows it without its files and must not save over it.
  saved.set('flaky', { openPaths: ['/flaky/a.ts'], activePath: '/flaky/a.ts' });
  await controller.restoreEditorWorkspaceForSession('flaky', '/flaky', false, signal);
  controller.releaseOwnership();
  await controller.restoreEditorWorkspaceForSession('hello', '/repo', true, signal);
  assert.deepEqual(saved.get('flaky'), { openPaths: ['/flaky/a.ts'], activePath: '/flaky/a.ts' }, 'leaving a failed selection keeps its saved tabs');
});
