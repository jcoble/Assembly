/**
 * gitPanelStore.test.ts — the source-control state and its service.
 *
 * `gitPanelStore.svelte.ts` calls `$state(...)` at module scope, which the
 * Svelte compiler normally rewrites. Node runs the file as plain JavaScript, so
 * the test supplies the one-line stand-in below before importing: `$state(v)`
 * returns `v` unchanged, which is exactly the semantics the store relies on
 * (a plain value bag with no reactivity of its own).
 */
globalThis.$state = (value) => value;

const assert = (await import('node:assert/strict')).default;

const store = await import('../src/lib/shell/git/gitPanelStore.svelte.ts');
const service = await import('../src/lib/shell/git/gitService.ts');
const { GIT_DIFF_TIMEOUT_MESSAGE } = await import(
  '../src/lib/shell/git/gitBackendExtra.ts'
);

const {
  buildGitStatusFileGroups,
  clearSelectedGitFile,
  createGitPanelState,
  describeGitBranch,
  describeGitFileChange,
  describeGitStatusGroups,
  gitFileTitle,
  gitStatusGroupActionLabel,
  hasStagedChanges,
  isGitFileDeleted,
  isNotARepositoryError,
  isPlainFolderError,
  repositoryLabel,
  resetGitPanelState
} = store;

const {
  COMMIT_HISTORY_LIMIT,
  DESKTOP_ONLY_MESSAGE,
  absolutePathWithin,
  createGitService,
  createRequestGuard,
  tauriGitBackend
} = service;

/** One changed-file record, shaped like the backend's `ProjectGitFileStatus`. */
function file(relativePath, indexStatus, worktreeStatus, badge) {
  return {
    relativePath,
    indexStatus,
    worktreeStatus,
    status: worktreeStatus || indexStatus,
    badge
  };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

// ── grouping ────────────────────────────────────────────────────────────────
{
  const files = [
    file('a.ts', 'modified', '', 'M'), // staged only
    file('b.ts', '', 'modified', 'M'), // changed on disk only
    file('c.ts', 'added', 'modified', 'M'), // both
    file('d.ts', '?', '?', '?') // new, not tracked yet
  ];

  const groups = buildGitStatusFileGroups(files);
  assert.deepEqual(
    groups.map((group) => [group.id, group.label, group.action, group.files.length]),
    [
      ['staged', 'Staged', 'unstage', 2],
      ['unstaged', 'Changed', 'stage', 2],
      ['untracked', 'New files', 'stage', 1]
    ]
  );
  assert.deepEqual(
    groups[0].files.map((entry) => entry.relativePath),
    ['a.ts', 'c.ts']
  );
  assert.deepEqual(
    groups[1].files.map((entry) => entry.relativePath),
    ['b.ts', 'c.ts'],
    'a file with staged AND unstaged work shows in both groups'
  );
  assert.deepEqual(
    groups[2].files.map((entry) => entry.relativePath),
    ['d.ts'],
    'an untracked file is never counted as staged'
  );

  assert.equal(describeGitStatusGroups(groups), 'Staged 2 · Changed 2 · New files 1');
  assert.equal(describeGitStatusGroups([]), 'No changed files');
  assert.deepEqual(buildGitStatusFileGroups([]), [], 'empty groups are dropped');
  assert.equal(gitStatusGroupActionLabel(groups[0]), 'Unstage all');
  assert.equal(gitStatusGroupActionLabel(groups[1]), 'Stage all');
  assert.equal(hasStagedChanges({ files }), true);
  assert.equal(hasStagedChanges({ files: [file('d.ts', '?', '?', '?')] }), false);
  assert.equal(hasStagedChanges(null), false);
}

// ── row descriptions ────────────────────────────────────────────────────────
{
  assert.equal(describeGitFileChange(file('c.ts', 'added', 'modified', 'M')), 'added + modified');
  assert.equal(describeGitFileChange(file('b.ts', '', 'modified', 'M')), 'modified');
  assert.equal(describeGitFileChange({ ...file('x.ts', '', '', ''), status: '' }), 'changed');
  assert.equal(
    gitFileTitle(file('c.ts', 'added', 'modified', 'M')),
    'c.ts\nStaged: added\nOn disk: modified'
  );
  assert.equal(gitFileTitle(file('d.ts', '', '', '')), 'd.ts');
  assert.equal(isGitFileDeleted(file('gone.ts', '', 'deleted', 'D')), true);
  assert.equal(isGitFileDeleted(file('here.ts', '', 'modified', 'M')), false);
}

// ── branch line + repository label ──────────────────────────────────────────
{
  assert.equal(
    describeGitBranch({ branch: 'main', ahead: 2, behind: 1, hasUpstream: true, files: [] }),
    'main · 2 to push · 1 to pull'
  );
  assert.equal(
    describeGitBranch({ branch: 'main', ahead: 0, behind: 0, hasUpstream: true, files: [] }),
    'main · up to date'
  );
  assert.equal(
    describeGitBranch({ branch: 'wip', ahead: 3, behind: 0, hasUpstream: false, files: [] }),
    'wip · no upstream branch'
  );
  assert.equal(
    describeGitBranch({ branch: null, ahead: 0, behind: 0, hasUpstream: false, files: [] }),
    'no branch checked out · no upstream branch'
  );
  assert.equal(describeGitBranch(null), 'No repository loaded');
  assert.equal(repositoryLabel('/Users/me/dev/thing/'), 'thing');
  assert.equal(repositoryLabel('/Users/me/dev/thing'), 'thing');
  assert.equal(repositoryLabel(null), '');
}

// ── state resets ────────────────────────────────────────────────────────────
{
  const state = createGitPanelState();
  state.status = { branch: 'main', ahead: 0, behind: 0, hasUpstream: true, files: [] };
  state.commitMessage = 'wip';
  state.selectedPath = 'a.ts';
  state.selectedPaths.compact = 'a.ts';
  state.diffOwner = 'compact';
  state.selectedDiff = { relativePath: 'a.ts', status: 'modified', diff: 'x', isBinary: false };

  clearSelectedGitFile(state);
  assert.equal(state.selectedPath, '');
  assert.deepEqual(state.selectedPaths, { compact: '', large: '' });
  assert.equal(state.selectedDiff, null);
  assert.equal(state.commitMessage, 'wip', 'clearing the diff keeps the draft commit message');

  state.selectedPaths = { compact: 'still-reading.ts', large: 'old-large.ts' };
  state.diffOwner = 'compact';
  state.diffLoading = true;
  const revision = state.diffRevision;
  clearSelectedGitFile(state, 'large');
  assert.equal(state.selectedPaths.large, '');
  assert.equal(state.selectedPaths.compact, 'still-reading.ts');
  assert.equal(state.diffLoading, true, 'clearing the other view keeps this diff read alive');
  assert.equal(state.diffRevision, revision, 'clearing the other view does not supersede this read');

  resetGitPanelState(state, '/repo');
  assert.equal(state.root, '/repo');
  assert.equal(state.status, null);
  assert.equal(state.commitMessage, '', 'switching repository drops the draft message');
  assert.equal(state.activated, false);
}

// ── request guard ───────────────────────────────────────────────────────────
{
  const guard = createRequestGuard();
  const first = guard.next();
  assert.equal(guard.isCurrent(first), true);
  const second = guard.next();
  assert.equal(guard.isCurrent(first), false, 'an older request is superseded');
  assert.equal(guard.isCurrent(second), true);
  guard.invalidate();
  assert.equal(guard.isCurrent(second), false, 'invalidate supersedes everything in flight');
}

// ── absolute path building ──────────────────────────────────────────────────
{
  assert.equal(absolutePathWithin('/repo', 'src/a.ts'), '/repo/src/a.ts');
  assert.equal(absolutePathWithin('/repo/', '/src/a.ts'), '/repo/src/a.ts');
  assert.equal(absolutePathWithin('/repo', ''), '/repo');
}

// ── a stub backend, so the service can be driven without a repository ───────
function makeBackend(overrides = {}) {
  const calls = [];
  const status = (files = []) => ({
    branch: 'main',
    ahead: 0,
    behind: 0,
    hasUpstream: true,
    files
  });
  const backend = {
    calls,
    status,
    async readStatus(root) {
      calls.push(['readStatus', root]);
      return status();
    },
    async readDiff(root, path) {
      calls.push(['readDiff', root, path]);
      return { relativePath: path, status: 'modified', diff: '', isBinary: false };
    },
    async readHistory(root, cursor, relativePath) {
      calls.push(['readHistory', root, cursor, relativePath]);
      return { commits: [], nextCursor: null, complete: true };
    },
    async listBranches(root) {
      calls.push(['listBranches', root]);
      return { current: 'main', branches: [], remotes: ['origin'] };
    },
    async stage(root, paths) {
      calls.push(['stage', root, paths]);
      return { message: `Staged ${paths.length} files`, status: status() };
    },
    async unstage(root, paths) {
      calls.push(['unstage', root, paths]);
      return { message: `Unstaged ${paths.length} files`, status: status() };
    },
    async commit(root, message) {
      calls.push(['commit', root, message]);
      return { message: 'Committed staged changes', status: status() };
    },
    async fetch(root) {
      calls.push(['fetch', root]);
      return { message: 'Fetched repository remotes', status: status() };
    },
    async pull(root) {
      calls.push(['pull', root]);
      return { message: 'Pulled fast-forward updates', status: status() };
    },
    async push(root) {
      calls.push(['push', root]);
      return { message: 'Pushed current branch', status: status() };
    },
    async addRemote(root, name, url) {
      calls.push(['addRemote', root, name, url]);
      return { message: `Added remote ${name}`, status: status() };
    },
    async publish(root, expectedBranch, remote) {
      calls.push(['publish', root, expectedBranch, remote]);
      return { message: 'Published branch', status: { ...status(), branch: expectedBranch } };
    }
  };
  return Object.assign(backend, overrides);
}

// ── activation points at a root; visible surfaces own their reads ───────────
{
  const backend = makeBackend();
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  assert.deepEqual(backend.calls, [], 'building the service calls no backend command');
  assert.equal(state.activated, false);

  git.activate('/repo');
  void git.refreshStatus();
  git.ensureHistorySurface();
  await settle();
  assert.deepEqual(
    backend.calls,
    [
      ['readStatus', '/repo'],
      ['readHistory', '/repo', null, null]
    ],
    'the visible status and history surfaces read once each'
  );
  assert.equal(state.activated, true);
  assert.equal(state.desktopOnly, false);
  assert.equal(state.statusLoading, false);
  assert.equal(state.status.branch, 'main');

  backend.calls.length = 0;
  git.activate('/repo');
  await settle();
  assert.deepEqual(backend.calls, [], 'activating again with the same repository does nothing');

  git.activate('/other');
  void git.refreshStatus();
  git.ensureHistorySurface();
  await settle();
  assert.equal(state.root, '/other');
  assert.deepEqual(backend.calls, [
    ['readStatus', '/other'],
    ['readHistory', '/other', null, null]
  ]);
}

// ── a failed history read is not repeated until refresh or a new folder ─────
// The visible surfaces call ensureHistorySurface() every time their effect
// re-runs, and a failed read re-runs it. Reading again there looped about 33
// times a second on a folder with no repository (TSK-1366).
{
  const backend = makeBackend({
    async readHistory(root, cursor, relativePath) {
      backend.calls.push(['readHistory', root, cursor, relativePath]);
      throw new Error('fatal: not a git repository (or any of the parent directories): .git');
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  const historyReads = () => backend.calls.filter((call) => call[0] === 'readHistory').length;

  git.activate('/plain');
  for (let run = 0; run < 3; run += 1) {
    git.ensureHistorySurface();
    await settle();
  }
  assert.equal(historyReads(), 1, 'the failed read is not repeated while the surface stays open');
  assert.match(state.historyError, /not a git repository/);

  await git.refreshHistory();
  assert.equal(historyReads(), 2, 'asking to refresh reads again');

  git.activate('/other');
  git.ensureHistorySurface();
  await settle();
  assert.equal(historyReads(), 3, 'a different folder reads again');
}

// ── a superseded status read never overwrites the newer one ─────────────────
{
  const resolvers = [];
  const backend = makeBackend({
    readStatus(root) {
      return new Promise((resolve) => resolvers.push({ root, resolve }));
    },
    async readHistory() {
      return [];
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  void git.refreshStatus();
  await settle();
  void git.refreshStatus();
  await settle();
  assert.equal(resolvers.length, 2, 'two status reads are in flight');

  // The NEWER read answers first, then the older one answers late.
  resolvers[1].resolve({
    branch: 'newer',
    ahead: 0,
    behind: 0,
    hasUpstream: true,
    files: []
  });
  await settle();
  assert.equal(state.status.branch, 'newer');
  assert.equal(state.statusLoading, false);

  resolvers[0].resolve({
    branch: 'older',
    ahead: 0,
    behind: 0,
    hasUpstream: true,
    files: []
  });
  await settle();
  assert.equal(state.status.branch, 'newer', 'the late answer from the older read is dropped');
}

// ── hiding the graph mid-read still lets the session's status load (TSK-1438) ─
// A session switch hides the graph (releaseHistorySurface) while the Changes
// tab's status read for the new session is in flight. Status was already null,
// so the tab sees no change and never asks again: the read must still land.
{
  const resolvers = [];
  const backend = makeBackend({
    readStatus(root) {
      return new Promise((resolve) => resolvers.push({ root, resolve }));
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  const answer = (branch) => ({ branch, ahead: 0, behind: 0, hasUpstream: true, files: [] });

  git.activate('/one');
  void git.refreshStatus();
  git.activate('/two');
  void git.refreshStatus();
  git.releaseHistorySurface();
  resolvers[0].resolve(answer('one'));
  await settle();
  assert.equal(state.status, null, "the old session's status never shows after the switch");
  resolvers[1].resolve(answer('two'));
  await settle();
  assert.equal(state.root, '/two');
  assert.equal(state.status?.branch, 'two', "the final session's status loads");
  assert.equal(state.statusLoading, false);
}

// ── switching repository mid-read drops the answer for the old one ──────────
{
  let resolveFirst = null;
  const backend = makeBackend({
    readStatus(root) {
      if (root === '/repo') return new Promise((resolve) => (resolveFirst = resolve));
      return Promise.resolve({
        branch: 'second',
        ahead: 0,
        behind: 0,
        hasUpstream: true,
        files: []
      });
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  void git.refreshStatus();
  await settle();
  git.activate('/second');
  void git.refreshStatus();
  await settle();
  resolveFirst({ branch: 'first', ahead: 0, behind: 0, hasUpstream: true, files: [] });
  await settle();

  assert.equal(state.root, '/second');
  assert.equal(state.status.branch, 'second', "the old repository's answer is discarded");
}

// ── an action adopts the fresh status the backend returned ──────────────────
{
  const backend = makeBackend();
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();
  backend.calls.length = 0;

  await git.stagePaths([' src/a.ts ', '', 'src/b.ts']);
  assert.deepEqual(
    backend.calls,
    [['stage', '/repo', ['src/a.ts', 'src/b.ts']]],
    'blank paths are dropped and the rest are trimmed; no extra status read follows'
  );
  assert.equal(state.actionStatus, 'Staged 2 files');
  assert.equal(state.actionBusy, '');
  assert.equal(state.status.branch, 'main');

  backend.calls.length = 0;
  await git.stagePaths(['   ']);
  assert.deepEqual(backend.calls, [], 'an all-blank path list runs no command');
}

// ── commit needs a message, clears the box, and reloads the history ─────────
{
  const backend = makeBackend();
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();
  backend.calls.length = 0;

  await git.commit();
  assert.deepEqual(backend.calls, [], 'an empty message never reaches the backend');
  assert.equal(state.actionError, 'Type a commit message first.');

  state.commitMessage = '  fix the thing  ';
  await git.commit();
  await settle();
  assert.deepEqual(backend.calls, [
    ['commit', '/repo', 'fix the thing'],
    ['readHistory', '/repo', null, null]
  ]);
  assert.equal(state.commitMessage, '', 'a successful commit clears the box');
  assert.equal(state.actionStatus, 'Committed staged changes');
}

// ── remote actions: fetch changes no commits, so it skips the history ───────
{
  const backend = makeBackend();
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  git.activate('/repo');
  await git.refreshStatus();
  git.ensureHistorySurface();
  await settle();

  backend.calls.length = 0;
  await git.runRemoteAction('fetch');
  await settle();
  assert.deepEqual(backend.calls, [['fetch', '/repo']]);

  backend.calls.length = 0;
  await git.runRemoteAction('pull');
  await settle();
  assert.deepEqual(backend.calls, [
    ['pull', '/repo'],
    ['readHistory', '/repo', null, null]
  ]);
}

// ── a failing command reports plainly and leaves the panel usable ───────────
{
  const backend = makeBackend({
    async push() {
      throw new Error('failed to push some refs');
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  git.activate('/repo');
  void git.refreshStatus();
  git.ensureHistorySurface();
  await settle();

  await git.runRemoteAction('push');
  assert.equal(state.actionError, 'failed to push some refs');
  assert.equal(state.actionBusy, '', 'the panel is not left stuck on a failed command');
  assert.equal(state.status.branch, 'main', 'the status already on screen survives');
}

// Adding a remote shares action errors and reloads history after success.
{
  const backend = makeBackend();
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  git.activate('/repo');
  assert.equal(await git.addRemote('/other', 'origin', '/bare'), false);
  state.actionBusy = 'fetch';
  assert.equal(await git.addRemote('/repo', 'origin', '/bare'), false);
  state.actionBusy = '';
  assert.deepEqual(backend.calls, [], 'skipped adds never call the backend');
  git.ensureHistorySurface();
  await settle();
  backend.calls.length = 0;
  assert.equal(await git.addRemote('/repo', 'origin', '/bare'), true);
  assert.equal(state.actionStatus, 'Added remote origin');
  assert.equal(state.status.branch, 'main', 'fresh status is adopted');
  assert.ok(backend.calls.some(([name]) => name === 'readHistory'), 'history reloads after add');
  backend.addRemote = async (root, name, url) => {
    backend.calls.push(['addRemote', root, name, url]);
    if (name === 'origin') throw new Error("error: remote origin already exists.");
    return { message: `Added remote ${name}`, status: state.status };
  };
  assert.equal(await git.addRemote('/repo', 'origin', '/bare'), false);
  assert.equal(state.actionError, 'error: remote origin already exists.');
  assert.equal(await git.addRemote('/repo', 'backup', '/bare'), true);
  assert.equal(state.actionError, '');
  assert.deepEqual(backend.calls.filter(([name]) => name === 'addRemote'), [
    ['addRemote', '/repo', 'origin', '/bare'],
    ['addRemote', '/repo', 'origin', '/bare'],
    ['addRemote', '/repo', 'backup', '/bare']
  ]);
  backend.addRemote = async () => {
    git.activate('/other');
    return { message: 'Added remote stale', status: { branch: 'old', files: [] } };
  };
  assert.equal(await git.addRemote('/repo', 'stale', '/bare'), false, 'a result for a folder that left is skipped');
  assert.notEqual(state.actionStatus, 'Added remote stale');
}

// ── publishing keeps the selected repository, branch and remote ──────────────
{
  const noUpstream = { branch: 'tsk-1233-test', ahead: 0, behind: 0, hasUpstream: false, files: [] };
  const backend = makeBackend({
    async readStatus() { return noUpstream; },
    async listBranches() { return { current: noUpstream.branch, branches: [], remotes: ['origin', 'backup'] }; }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  git.activate('/repo');
  await git.refreshStatus();

  const choices = await git.listBranches();
  assert.deepEqual(choices.remotes, ['origin', 'backup']);
  assert.equal(backend.calls.some(([name]) => name === 'publish'), false, 'listing remotes does not choose one');
  await git.publishBranch('/repo', noUpstream.branch, '');
  assert.match(state.actionError, /no configured remote/i);
  await git.publishBranch('/other-session-repo', noUpstream.branch, 'origin');
  assert.equal(backend.calls.some(([name]) => name === 'publish'), false, 'a different repository cannot publish');
  await git.publishBranch('/repo', 'another-branch', 'origin');
  assert.match(state.actionError, /branch changed/i);
  assert.equal(backend.calls.some(([name]) => name === 'publish'), false, 'a different branch cannot publish');

  let attempts = 0;
  backend.publish = async (root, branch, remote) => {
    backend.calls.push(['publish', root, branch, remote]);
    if (++attempts === 1) throw new Error('rejected: non-fast-forward');
    return { message: 'Published branch', status: { ...noUpstream, hasUpstream: true } };
  };
  await git.publishBranch('/repo', noUpstream.branch, 'backup');
  assert.equal(state.actionError, 'rejected: non-fast-forward');
  assert.equal(state.actionBusy, '');
  assert.equal(state.status.hasUpstream, false);
  await git.publishBranch('/repo', noUpstream.branch, 'backup');
  assert.equal(state.actionError, '');
  assert.equal(state.status.hasUpstream, true);
  assert.deepEqual(backend.calls.filter(([name]) => name === 'publish'), [
    ['publish', '/repo', noUpstream.branch, 'backup'],
    ['publish', '/repo', noUpstream.branch, 'backup']
  ]);
  await git.runRemoteAction('push');
  assert.ok(backend.calls.some(([name, root]) => name === 'push' && root === '/repo'), 'later pushes use normal Push');
}

{
  let resolveBranches;
  const backend = makeBackend({
    async listBranches() { return new Promise((resolve) => { resolveBranches = resolve; }); }
  });
  const git = createGitService({ backend, state: createGitPanelState() });
  git.activate('/first');
  const pending = git.listBranches();
  git.activate('/second');
  resolveBranches({ current: 'first', branches: [], remotes: ['origin'] });
  assert.equal(await pending, null, 'a remote list from the previous session root is discarded');
}

{
  const remoteRoot = 'assembly-remote://workbox/repo';
  let response = { current: 'main', branches: [] };
  const backend = makeBackend({ async listBranches() { return response; } });
  const git = createGitService({ backend, state: createGitPanelState() });
  git.activate(remoteRoot);

  await assert.rejects(git.listBranches(), {
    message: 'The remote backend is older than this Assembly app. Update the backend in Settings > Connections, then retry Publish.'
  });
  response = { current: 'main', branches: [], remotes: [] };
  assert.deepEqual((await git.listBranches()).remotes, [], 'a real empty remote list remains empty');
  response = { current: 'main', branches: [], remotes: ['origin'] };
  assert.deepEqual((await git.listBranches()).remotes, ['origin'], 'configured remotes pass through');

  git.activate('/local-repo');
  response = { current: 'main', branches: [] };
  assert.equal((await git.listBranches()).remotes, undefined, 'the guard applies only to remote backends');
}

// ── selecting a file loads its diff, including deleted tracked files ─────────
{
  const backend = makeBackend();
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  git.activate('/repo');
  await settle();
  backend.calls.length = 0;

  await git.selectFile(file('src/a.ts', '', 'modified', 'M'));
  assert.deepEqual(backend.calls, [['readDiff', '/repo', '/repo/src/a.ts']]);
  assert.equal(state.selectedPath, 'src/a.ts');
  assert.equal(state.selectedPaths.compact, 'src/a.ts');
  assert.equal(state.diffLoading, false);
  assert.equal(state.diffError, '');

  backend.calls.length = 0;
  await git.selectFile(file('src/gone.ts', '', 'deleted', 'D'));
  assert.deepEqual(backend.calls, [['readDiff', '/repo', '/repo/src/gone.ts']]);
  assert.equal(state.selectedDiff?.relativePath, '/repo/src/gone.ts');
  assert.equal(state.diffError, '');

  await git.selectFile(file('src/large.ts', '', 'modified', 'M'), 'large');
  assert.equal(state.selectedPaths.compact, 'src/gone.ts', 'large does not replace compact');
  assert.equal(state.selectedPaths.large, 'src/large.ts');
  assert.equal(state.diffOwner, 'large');

  git.clearSelection('large');
  assert.equal(state.selectedPaths.compact, 'src/gone.ts');
  assert.equal(state.selectedPaths.large, '');
  assert.equal(state.selectedPath, '');

  git.clearSelection();
  assert.equal(state.selectedPath, '');
  assert.equal(state.diffError, '');
}

// ── a diff read that never answers ends in an error, not a spinner ───────
{
  const backend = makeBackend({
    async readDiff() {
      return new Promise(() => {});
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state, diffTimeoutMs: 5 });
  git.activate('/repo');

  // Node does not wait on AbortSignal.timeout, so hold the process open until it fires.
  const keepAlive = setInterval(() => {}, 1000);
  await git.selectFile(file('src/stuck.ts', '', 'modified', 'M'));
  clearInterval(keepAlive);

  assert.equal(state.diffLoading, false, 'a stalled diff read ends its spinner');
  assert.equal(state.diffError, GIT_DIFF_TIMEOUT_MESSAGE);
  assert.equal(state.selectedDiff, null);
}

// ── outside the desktop app: say so, load nothing, invent nothing ───────────
{
  const backend = makeBackend({
    async readStatus() {
      return null;
    },
    async readHistory() {
      return null;
    },
    async stage() {
      return null;
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  void git.refreshStatus();
  git.ensureHistorySurface();
  await settle();
  assert.equal(state.desktopOnly, true);
  assert.equal(state.status, null, 'no stand-in repository is invented');
  assert.deepEqual(state.history, []);
  assert.equal(state.statusError, '', 'a missing desktop app is not an error message');

  await git.stagePaths(['a.ts']);
  assert.equal(state.actionStatus, DESKTOP_ONLY_MESSAGE);
}

// ── the real backend counts one invoke per command, by its Tauri name ───────
{
  const counted = [];
  const backend = tauriGitBackend((command) => counted.push(command));

  // Node is not the desktop app, so every wrapper short-circuits to null; what
  // is being checked here is the counting, and that no command is missed.
  const results = await Promise.all([
    backend.readStatus('/repo'),
    backend.readDiff('/repo', '/repo/a.ts'),
    backend.readHistory('/repo', COMMIT_HISTORY_LIMIT),
    backend.stage('/repo', ['a.ts']),
    backend.unstage('/repo', ['a.ts']),
    backend.commit('/repo', 'message'),
    backend.fetch('/repo'),
    backend.pull('/repo'),
    backend.push('/repo'),
    backend.publish('/repo', 'tsk-1233-test', 'origin')
  ]);

  assert.deepEqual(counted, [
    'project_git_status',
    'read_source_git_diff',
    'read_git_commit_history',
    'stage_git_paths',
    'unstage_git_paths',
    'commit_git_repository',
    'fetch_git_repository',
    'pull_git_repository',
    'push_git_repository',
    'publish_git_repository'
  ]);
  assert.deepEqual(
    results,
    Array(10).fill(null),
    'outside the desktop app every command answers "nothing here"'
  );
}

// A folder with no repository in it is an ordinary thing to be looking at, and
// the panel says so in a sentence instead of showing git's raw stderr. Every
// other failure stays an error with its own message — matching too eagerly here
// would hide real breakage behind a calm empty state.
{
  const notRepos = [
    'fatal: not a git repository (or any of the parent directories): .git',
    'fatal: Not a Git repository',
    'Could not read the repository status.\nfatal: not a git repository',
    'NOT A GIT REPOSITORY',
    // A bare repository has no working copy to show either.
    'fatal: this operation must be run in a work tree'
  ];
  for (const message of notRepos) {
    assert.equal(isNotARepositoryError(message), true, `"${message}" means no repository`);
  }

  const realFailures = [
    '',
    'Could not read the repository status.',
    'fatal: unable to read config file',
    "error: pathspec 'nope' did not match any file(s) known to git",
    'fatal: could not read Username for https://github.com: terminal prompts disabled',
    'Permission denied (publickey).',
    'fatal: your current branch appears to be broken'
  ];
  for (const message of realFailures) {
    assert.equal(isNotARepositoryError(message), false, `"${message}" is a real failure`);
  }

  // Only a folder with no repository at all may be initialized; a bare
  // repository is one already.
  assert.equal(isPlainFolderError('fatal: not a git repository (or any of the parent directories): .git'), true);
  assert.equal(isPlainFolderError('fatal: this operation must be run in a work tree'), false);
  for (const message of realFailures) {
    assert.equal(isPlainFolderError(message), false, `"${message}" does not offer Initialize`);
  }
}

// ── initializing a plain folder clears both stored "not a repository" errors ──
{
  const notARepo = 'fatal: not a git repository (or any of the parent directories): .git';
  let initialized = false;
  const backend = makeBackend({
    async readStatus(root) {
      backend.calls.push(['readStatus', root]);
      if (!initialized) throw new Error(notARepo);
      return backend.status();
    },
    async readHistory(root, cursor, relativePath) {
      backend.calls.push(['readHistory', root, cursor, relativePath]);
      if (!initialized) throw new Error(notARepo);
      return { commits: [], nextCursor: null, complete: true };
    },
    async initRepository(root, signal) {
      backend.calls.push(['initRepository', root, signal.aborted]);
      initialized = true;
    }
  });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });
  const root = 'assembly-remote://profile-1/home/me/plain';

  git.activate(root);
  await git.refreshStatus();
  git.ensureHistorySurface();
  await settle();
  assert.match(state.statusError, /not a git repository/);
  assert.match(state.historyError, /not a git repository/);

  backend.calls.length = 0;
  await git.initRepository(new AbortController().signal);
  assert.deepEqual(backend.calls[0], ['initRepository', root, false], 'the machine-qualified root is passed unchanged');
  assert.equal(state.statusError, '');
  assert.equal(state.historyError, '');
  assert.equal(state.status.branch, 'main');
  assert.equal(state.actionBusy, '');
  assert.equal(state.actionError, '');
}

// ── the panel's three sections, its remote actions and its scope picker ──────
{
  const sections = await import(
    '../src/lib/shell/git/components/sourceControl/sourceControlSections.ts'
  );
  const panelActions = await import(
    '../src/lib/shell/git/components/sourceControl/sourceControlPanelActions.ts'
  );

  const file = (relativePath, indexStatus, worktreeStatus, badge) => ({
    relativePath,
    indexStatus,
    worktreeStatus,
    status: worktreeStatus || indexStatus,
    badge
  });

  // A file staged and then edited again is in two places at once, and the panel
  // says so rather than picking one — that is what git itself reports.
  const bothWays = file('src/both.ts', 'modified', 'modified', 'M');
  const stagedOnly = file('src/staged.ts', 'modified', '', 'M');
  const changedOnly = file('src/changed.ts', '', 'modified', 'M');
  const untracked = file('src/new.ts', '?', '?', '?');

  const split = sections.sourceControlSections({
    branch: 'main',
    ahead: 0,
    behind: 0,
    hasUpstream: true,
    files: [changedOnly, untracked, bothWays, stagedOnly]
  });

  assert.deepEqual(
    split.map((section) => section.id),
    ['staged', 'changed', 'untracked'],
    'the panel draws staged, then changed, then untracked'
  );
  assert.deepEqual(
    split.map((section) => section.label),
    ['Staged', 'Changes', 'Untracked'],
    'each section is named the way git names it'
  );
  assert.deepEqual(
    split[0].files.map((entry) => entry.relativePath),
    ['src/both.ts', 'src/staged.ts'],
    'staged holds everything with work in the index, sorted by path'
  );
  assert.deepEqual(
    split[1].files.map((entry) => entry.relativePath),
    ['src/both.ts', 'src/changed.ts'],
    'changed holds tracked files with unstaged work, including the one in both'
  );
  assert.deepEqual(
    split[2].files.map((entry) => entry.relativePath),
    ['src/new.ts'],
    'untracked holds only files git does not know about'
  );

  // Discard All deletes every untracked file, including ones past the list's
  // cap, so the question counts the hidden files as untracked: it may overstate
  // the number but never understates it.
  assert.equal(
    sections.discardAllUntrackedCount({ branch: 'main', ahead: 0, behind: 0, hasUpstream: true, files: [changedOnly, untracked, bothWays] }),
    1,
    'an uncapped list counts its untracked files'
  );
  assert.equal(
    sections.discardAllUntrackedCount({ branch: 'main', ahead: 0, behind: 0, hasUpstream: true, files: [changedOnly, bothWays], totalFiles: 19000 }),
    18998,
    'a capped list counts every hidden file as untracked'
  );
  assert.equal(sections.discardAllUntrackedCount(null), 0, 'no status, nothing to delete');

  const empty = sections.sourceControlSections(null);
  assert.deepEqual(
    empty.map((section) => section.files.length),
    [0, 0, 0],
    'no status at all still draws the three empty sections'
  );

  // Pull and push: off with a reason, never off in silence.
  const remoteContext = (over = {}) => ({
    canWrite: true,
    readOnlyReason: 'This page can read the repository but not change it.',
    busy: false,
    remotes: null,
    ...over
  });
  const withUpstream = { branch: 'main', ahead: 1, behind: 2, hasUpstream: true, files: [] };
  const noUpstream = { branch: 'work', ahead: 0, behind: 0, hasUpstream: false, files: [] };

  const ready = panelActions.sourceControlRemoteActions(withUpstream, '/repo', remoteContext());
  assert.deepEqual(
    ready.map((action) => action.id),
    ['fetch', 'pull', 'push'],
    'the more-actions menu offers fetch, pull and push in that order'
  );
  assert.ok(
    ready.every((action) => action.enabled && action.disabledReason === null),
    'a writable repository with an upstream can run all three'
  );

  const detached = panelActions.sourceControlRemoteActions(noUpstream, '/repo', remoteContext());
  assert.equal(detached[0].enabled, false);
  assert.equal(detached[0].disabledReason, 'Reading this repository’s remotes…');
  const emptyRemotes = panelActions.sourceControlRemoteActions(noUpstream, '/repo', remoteContext({ remotes: [] }));
  assert.equal(emptyRemotes[0].enabled, false);
  assert.equal(emptyRemotes[0].disabledReason, 'This repository has no remote. Add one to fetch.');
  assert.equal(panelActions.sourceControlRemoteActions(noUpstream, '/repo', remoteContext({ remotes: ['origin'] }))[0].enabled, true);
  assert.equal(panelActions.sourceControlRemoteActions({ ...noUpstream, branch: null }, '/repo', remoteContext({ remotes: ['origin'] }))[0].enabled, true, 'detached HEAD can fetch');
  for (const remotes of [null, [], ['origin']]) {
    assert.equal(panelActions.sourceControlRemoteActions(withUpstream, '/repo', remoteContext({ remotes }))[0].enabled, true);
  }
  for (const action of detached.slice(1)) {
    assert.equal(action.enabled, false, `${action.id} needs an upstream`);
    assert.match(
      action.disabledReason ?? '',
      /upstream/i,
      `${action.id} says the branch has no upstream`
    );
  }
  const publish = panelActions.sourceControlPublishAction(noUpstream, '/repo', remoteContext());
  assert.equal(publish.enabled, true, 'a named branch without upstream can publish');
  assert.equal(panelActions.sourceControlPublishAction(withUpstream, '/repo', remoteContext()).enabled, false);
  assert.equal(panelActions.sourceControlPublishAction(null, '/repo', remoteContext()).enabled, false);
  assert.equal(panelActions.sourceControlPublishAction(noUpstream, '/repo', remoteContext({ canWrite: false })).enabled, false);

  const rootless = panelActions.sourceControlRemoteActions(withUpstream, '  ', remoteContext());
  assert.ok(
    rootless.every((action) => !action.enabled && action.disabledReason !== null),
    'with no repository folder every remote action is off with a reason'
  );

  const readOnly = panelActions.sourceControlRemoteActions(
    noUpstream,
    '/repo',
    remoteContext({ canWrite: false, busy: true })
  );
  assert.equal(
    readOnly[0].disabledReason,
    'This page can read the repository but not change it.',
    'a page that cannot write repeats the panel words'
  );

  const working = panelActions.sourceControlRemoteActions(
    noUpstream,
    '/repo',
    remoteContext({ busy: true })
  );
  assert.ok(
    working.every((action) => !action.enabled && action.disabledReason === 'Wait for the current source-control action to finish.'),
    'nothing else starts while a source-control action is running'
  );

  // The scope picker: the session folder first, then the other checkouts.
  const worktrees = [
    { path: '/repo', branch: 'main' },
    { path: '/work/tsk-1', branch: 'tsk-1-thing' },
    { path: '/work/tsk-2', branch: '' }
  ];
  const options = panelActions.sourceControlScopeOptions('/repo', worktrees, false);
  assert.deepEqual(
    options.map((option) => option.path),
    ['/repo', '/work/tsk-1', '/work/tsk-2'],
    'the session folder leads and is never listed twice'
  );
  assert.match(options[0].label, /session/i, 'the first option says it is the session folder');
  assert.equal(options[1].label, 'tsk-1 — tsk-1-thing', 'a checkout shows its folder and branch');
  assert.equal(options[2].label, 'tsk-2', 'a checkout with no branch name shows just the folder');

  assert.deepEqual(
    panelActions.sourceControlScopeOptions('', worktrees, false).map((option) => option.path),
    ['/repo', '/work/tsk-1', '/work/tsk-2'],
    'with no session folder there is nothing to lead with, only the checkouts'
  );

  assert.equal(
    panelActions.isSourceControlScopeReadOnly('/repo', '/repo'),
    false,
    'the session folder itself is the writable scope'
  );
  assert.equal(
    panelActions.isSourceControlScopeReadOnly('/repo', ''),
    false,
    'no choice made yet means the session folder'
  );
  assert.equal(
    panelActions.isSourceControlScopeReadOnly('/repo', '/work/tsk-1'),
    true,
    'another checkout is read-only'
  );
  assert.match(
    panelActions.describeSourceControlScope('/work/tsk-1'),
    /^Viewing \/work\/tsk-1 \(read-only\)$/,
    'the note names the folder being read'
  );
}

console.log('gitPanelStore: all tests passed');
