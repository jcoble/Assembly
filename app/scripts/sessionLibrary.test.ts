import assert from 'node:assert/strict';

import {
  buildSessionLibrary,
  canonicalCwd,
  deriveOwnedLibraryState,
  filterSessionLibrary,
  groupSessionLibrary,
  sessionIdentityKey,
  toggleSessionLibraryExpansion
} from '../src/lib/shell/history/library/sessionLibraryModel.ts';
import { sessionContextMenuRoster } from '../src/lib/shell/history/library/sessionLibraryContextMenu.ts';
import {
  createSessionLibraryMountCoordinator,
  createSessionLibraryService,
  registerSessionLibraryHost,
  sessionLibraryHost
} from '../src/lib/shell/history/library/sessionLibraryService.ts';

function owned(ownedId, cwd, extra = {}) {
  return {
    ownedId,
    agent: 'codex',
    viaCmux: false,
    source: 'fresh',
    title: 'Same title',
    projectGroupKey: 'none',
    projectGroupLabel: 'No project',
    cwd,
    resumeCommand: null,
    nativeSessionId: 'native-1',
    ptySessionId: null,
    state: 'background',
    completedAt: null,
    settledAt: null,
    branch: null,
    taskId: null,
    pullRequest: null,
    messageCount: null,
    latestTurnPreview: null,
    lastActivity: null,
    ...extra
  };
}

function available(id, cwd, extra = {}) {
  return {
    provider: 'codex',
    id,
    title: 'Same title',
    description: null,
    model: 'o4-mini',
    projectPath: cwd,
    projectGroupKey: 'none',
    projectGroupLabel: 'No project',
    lastActivity: null,
    resumeCommands: [],
    ...extra
  };
}

// Canonical identity never uses title: same-title sessions in two worktrees
// remain distinct, while separator spelling and trailing slashes normalize.
{
  assert.equal(canonicalCwd('/repo/worktree/'), '/repo/worktree');
  assert.equal(canonicalCwd('C:\\Repo\\Worktree\\'), 'c:/Repo/Worktree');
  assert.notEqual(
    sessionIdentityKey({ provider: 'codex', nativeSessionId: 'n', cwd: '/one' }),
    sessionIdentityKey({ provider: 'codex', nativeSessionId: 'n', cwd: '/two' })
  );
}

// OwnedId wins first; then the provider/native/cwd tuple deduplicates provider
// rows. A same-title different-worktree row is not dropped.
{
  const first = owned('owned-1', '/one');
  const duplicateOwned = owned('owned-1', '/duplicate');
  const second = owned('owned-2', '/two');
  const duplicateIdentity = owned('owned-3', '/two');
  const rows = buildSessionLibrary(
    [first, duplicateOwned, second, duplicateIdentity],
    [available('native-1', '/one'), available('native-2', '/three')]
  );
  assert.deepEqual(rows.filter((row) => row.source === 'owned').map((row) => row.ownedId), [
    'owned-1',
    'owned-2'
  ]);
  assert.equal(rows.filter((row) => row.source === 'provider').length, 1);
  assert.equal(rows.filter((row) => row.source === 'owned').length, 2, 'compound identity dedupes owned rows too');
  assert.equal(rows.at(-1).canonicalCwd, '/three');
}

// Same native identity and folder on different machines are different sessions.
{
  const rows = buildSessionLibrary([
    owned('local', '/same'),
    owned('remote', '/same', { executionEnvironment: 'remote', remoteProfileId: 'box', model: 'saved-model' })
  ], [available('native-1', '/same')]);
  assert.equal(rows.length, 2);
  assert.deepEqual(rows.map((row) => row.machine), ['local', 'box']);
  assert.equal(rows[1].model, 'saved-model');
}

// Settled is explicit settledAt semantics; age/title/process fields do not move
// a record between shelves.
{
  assert.equal(deriveOwnedLibraryState(owned('working', '/one')), 'working');
  assert.equal(
    deriveOwnedLibraryState(owned('done', '/one', { completedAt: '2000-01-01T00:00:00Z' })),
    'done'
  );
  assert.equal(
    deriveOwnedLibraryState(
      owned('settled', '/one', { state: 'live', settledAt: '2020-01-01T00:00:00Z' })
    ),
    'settled'
  );
  assert.equal(deriveOwnedLibraryState(owned('legacy', '/one', { settledAt: undefined })), 'working');
  assert.equal(deriveOwnedLibraryState(owned('invalid', '/one', { settledAt: '' })), 'working');
}

// Search/filter, grouping and paging remain pure projections.
{
  const rows = buildSessionLibrary(
    [owned('one', '/repo/one'), owned('two', '/repo/two', { completedAt: '2026-01-01T00:00:00Z' })],
    [available('three', '/repo/three')]
  );
  assert.equal(filterSessionLibrary(rows, { worktree: 'two' }).length, 1);
  assert.equal(filterSessionLibrary(rows, { state: 'resumable' }).length, 1);
  assert.deepEqual(groupSessionLibrary(rows).map((group) => group.state), [
    'working',
    'resumable',
    'done'
  ]);

  // Each row carries the project group SQL gave it, owned and scanned alike,
  // and the project filter and search match the group's label.
  const assembly = { projectGroupKey: 'project:p1', projectGroupLabel: 'Assembly' };
  const grouped = buildSessionLibrary([owned('g', '/repo/g', assembly)], [available('h', '/repo/h', assembly)]);
  assert.deepEqual(grouped.map((row) => [row.projectGroupKey, row.projectGroupLabel]), [
    ['project:p1', 'Assembly'],
    ['project:p1', 'Assembly']
  ]);
  assert.equal(filterSessionLibrary([...rows, ...grouped], { project: 'assembly' }).length, 2);
  assert.equal(filterSessionLibrary([...rows, ...grouped], { query: 'assembly' }).length, 2);
  assert.equal(toggleSessionLibraryExpansion(null, rows[0].key), rows[0].key);
  assert.equal(toggleSessionLibraryExpansion(rows[0].key, rows[0].key), null);
}

// Every context action stays visible. The current rail supports resume/archive/
// delete for owned rows, while missing native actions remain explicitly disabled.
{
  const row = buildSessionLibrary([owned('menu', '/repo/menu')], [])[0];
  const roster = sessionContextMenuRoster({ target: 'session', record: row });
  assert.deepEqual(roster.map((item) => item.label), [
    'Resume in Worktree',
    'Continue in New Session',
    'View Log',
    'Copy ID',
    'Archive',
    'Delete'
  ]);
  assert.equal(roster.find((item) => item.id === 'resume-worktree').enabled, true);
  assert.equal(roster.find((item) => item.id === 'continue-new-session').enabled, false);
  assert.match(roster.find((item) => item.id === 'view-log').disabledReason, /Future-native/);
  assert.equal(sessionContextMenuRoster({ target: 'center-tab' }).find((item) => item.id === 'copy-id').enabled, true);
}

// Service construction is inert. Refresh/actions call injected effects only
// when explicitly requested, so center/right movement cannot duplicate IO.
{
  let listed = 0;
  const actions = [];
  const service = createSessionLibraryService(
    { listProviderSessions: async () => { listed += 1; return [available('native', '/repo')]; } },
    {
      onOpen: (record) => actions.push(`open:${record.key}`),
      onResume: (record) => actions.push(`resume:${record.key}`)
    }
  );
  assert.equal(listed, 0);
  const rows = await service.refresh();
  assert.equal(listed, 1);
  await service.open(rows[0]);
  await service.resume(rows[0]);
  assert.deepEqual(actions, [`open:${rows[0].key}`, `resume:${rows[0].key}`]);
}

// History holds only the rows belonging to projects the reader opened. Closing
// one project releases its rows; closing the panel releases every held row.
{
  const first = available('first', '/repo/first', { projectGroupKey: 'project:repo', projectGroupLabel: 'repo' });
  const firstSibling = available('first-sibling', '/repo/second', { projectGroupKey: 'project:repo', projectGroupLabel: 'repo' });
  const second = available('second', '/other/second', { projectGroupKey: 'project:other', projectGroupLabel: 'other' });
  const allRows = buildSessionLibrary([], [first, firstSibling, second]);
  const firstKeys = new Set(allRows.slice(0, 2).map((row) => row.key));
  const secondKeys = new Set([allRows[2].key]);
  const service = createSessionLibraryService({
    listProviderSessions: async () => [first, firstSibling, second]
  });

  assert.equal(service.records.length, 0);
  await service.refresh(firstKeys);
  assert.deepEqual(service.records.map((row) => row.key), [...firstKeys]);
  await service.refresh(secondKeys);
  assert.deepEqual(new Set(service.records.map((row) => row.key)), new Set([...firstKeys, ...secondKeys]));
  service.release(firstKeys);
  assert.deepEqual(service.records.map((row) => row.key), [...secondKeys]);
  service.release();
  assert.equal(service.records.length, 0);
}

// Opening a project reuses the summary records already in the rail. Opening one
// card reads only that transcript for details instead of rescanning the project.
{
  const cached = available('cached', '/repo', {
    logPath: '/logs/cached.jsonl',
    latestTurns: undefined
  });
  let projectScans = 0;
  let detailReads = 0;
  const service = createSessionLibraryService({
    getAvailableSessions: () => [cached],
    listProviderSessions: async () => { projectScans += 1; return []; },
    readProviderSessionDetails: async () => {
      detailReads += 1;
      return [available('cached', '/repo', {
        logPath: '/logs/cached.jsonl',
        latestTurns: [{ speaker: 'agent', text: 'Finished.' }]
      })];
    }
  });
  const key = buildSessionLibrary([], [cached])[0].key;
  assert.equal((await service.refresh(new Set([key]))).length, 1);
  assert.equal(projectScans, 0);
  const details = await service.refresh(new Set([key]), { projectPath: '/repo' }, { includeDetails: true });
  assert.equal(details[0].latestTurns[0].text, 'Finished.');
  assert.equal(detailReads, 1);
  assert.equal(projectScans, 0);
}

// Remote cards read only saved data, including when that copy is empty.
{
  let reads = 0;
  const remote = owned('saved', '/same', { executionEnvironment: 'remote', remoteProfileId: 'box', messageCount: 100 });
  const service = createSessionLibraryService({
    getOwnedSessions: () => [remote],
    listProviderSessions: async () => { assert.fail('remote detail scanned local sessions'); },
    readProviderSessionDetails: async () => { assert.fail('remote detail read local transcript'); },
    readRemoteSavedSessionDetails: async (profile, id, signal) => {
      assert.equal(profile, 'box'); assert.equal(id, 'saved'); assert.equal(signal.aborted, false);
      reads += 1;
      return reads === 1 ? null : {
        turnCount: reads === 2 ? null : 3, firstPrompt: null, startComplete: false, latestTurns: []
      };
    }
  });
  const key = buildSessionLibrary([remote], [])[0].key;
  const rows = await service.refresh(new Set([key]), undefined, { includeDetails: true, signal: new AbortController().signal });
  assert.equal(reads, 1);
  assert.equal(rows[0].messageCount, null);
  assert.equal(rows[0].startComplete, undefined);
  assert.equal(rows[0].firstPrompt, null);
  assert.deepEqual(rows[0].latestTurns, []);
  const unknown = await service.refresh(new Set([key]), undefined, { includeDetails: true, signal: new AbortController().signal });
  assert.equal(unknown[0].messageCount, null);
  assert.equal(unknown[0].startComplete, false);
  const saved = await service.refresh(new Set([key]), undefined, { includeDetails: true, signal: new AbortController().signal });
  assert.equal(saved[0].messageCount, 3);
  assert.equal(saved[0].startComplete, false);
}

// Center/right placement is one runtime host lease over the same service/store;
// construction and movement never call the provider adapter or create a second
// lease while the first host is live.
{
  let listed = 0;
  const service = createSessionLibraryService({
    listProviderSessions: async () => { listed += 1; return []; }
  });
  const mounts = createSessionLibraryMountCoordinator();
  const center = mounts.mount('center');
  assert.equal(mounts.mountedPlacement, 'center');
  assert.equal(center.active, true);
  assert.throws(() => mounts.mount('right'), /already mounted/);
  const right = mounts.move('right');
  assert.equal(center.active, false);
  assert.equal(right.active, true);
  assert.equal(mounts.mountedPlacement, 'right');
  right.release();
  assert.equal(mounts.mountedPlacement, null);
  assert.equal(listed, 0);
  assert.equal(typeof service.refresh, 'function');
}

// A panel can be constructed before the route registers its live host. The
// captured handle must still begin forwarding once registration lands.
{
  const captured = sessionLibraryHost();
  const live = createSessionLibraryService({
    listProviderSessions: async () => [available('late-host', '/repo')]
  });
  const release = registerSessionLibraryHost({ service: live });
  assert.equal((await captured.service.refresh()).length, 1);
  release();
  assert.equal((await captured.service.refresh()).length, 0);
}

console.log('sessionLibrary: all tests passed');
