import assert from 'node:assert/strict';

import {
  buildSessionHistoryViewModel,
  createSessionHistoryCollapseState,
  filterSessionHistoryRecords,
  isSessionHistoryGroupOpen,
  toggleSessionHistoryGroup,
  visibleSessionHistoryRows
} from '../src/lib/shell/history/sessionHistoryViewModel.ts';
import type { SessionLibraryRecord } from '../src/lib/shell/history/library/sessionLibraryModel.ts';

const ATLAS = { projectGroupKey: 'repo:github.com/dev/atlas', projectGroupLabel: 'atlas' };

function record(key: string, input: Record<string, unknown> = {}): SessionLibraryRecord {
  return {
    key,
    source: 'provider',
    machine: 'local',
    ownedId: null,
    provider: 'alpha',
    nativeSessionId: key,
    canonicalCwd: '/Users/dev/work/atlas',
    title: `Session ${key}`,
    description: null,
    ...ATLAS,
    model: null,
    state: 'resumable',
    runtimeState: null,
    lastActivity: null,
    updatedAt: null,
    messageCount: null,
    firstPrompt: null,
    latestTurns: [],
    owned: null,
    available: null,
    ...input
  } as unknown as SessionLibraryRecord;
}

const rows = [
  record('atlas-main', {
    title: 'Review the navigation',
    updatedAt: '2026-08-08T12:00:00Z',
    messageCount: 4
  }),
  record('atlas-lane-old', {
    canonicalCwd: '/Users/dev/work/worktrees/atlas/feature-one',
    title: 'Investigate the cache',
    firstPrompt: 'Investigate cache invalidation in the editor',
    updatedAt: '2026-08-09T12:00:00Z',
    messageCount: 8
  }),
  record('atlas-lane-new', {
    canonicalCwd: '/Users/dev/work/worktrees/atlas/feature-one',
    title: 'Finish the cache repair',
    updatedAt: '2026-08-10T12:00:00Z',
    messageCount: 11
  }),
  record('beacon', {
    provider: 'beta',
    canonicalCwd: '/Users/dev/work/beacon',
    projectGroupKey: 'project:beacon',
    projectGroupLabel: 'beacon',
    title: '',
    nativeSessionId: 'beacon',
    firstPrompt: 'Trace the first visible message',
    updatedAt: '2026-08-07T12:00:00Z',
    messageCount: 2
  }),
  record('cedar-live', {
    source: 'owned',
    ownedId: 'cedar-live',
    provider: 'beta',
    canonicalCwd: '/Users/dev/work/cedar',
    projectGroupKey: 'project:cedar',
    projectGroupLabel: 'cedar',
    title: 'Keep the live session visible',
    state: 'working',
    runtimeState: 'working',
    updatedAt: '2026-08-06T12:00:00Z',
    owned: { state: 'live' }
  })
];

// All separates machines; scoped filters never cross machines with identical paths.
{
  const local = record('local');
  const remote = record('remote', { machine: 'box' });
  const view = buildSessionHistoryViewModel([local, remote]);
  assert.deepEqual(view.machines.map((machine) => machine.key), ['local', 'box']);
  assert.equal(view.showMachines, true);
  assert.equal(view.projects.length, 2);
  assert.notEqual(view.projects[0].key, view.projects[1].key);
  assert.equal(buildSessionHistoryViewModel([local]).showMachines, false);
  for (const scope of ['workspace', 'project'] as const) {
    assert.deepEqual(filterSessionHistoryRecords([local, remote], {
      scope, machine: 'box', workspacePath: local.canonicalCwd, projectKey: ATLAS.projectGroupKey
    }).map((row) => row.key), ['remote']);
  }
}

// The shortest unique suffix is used even when three checkouts share a basename.
{
  const view = buildSessionHistoryViewModel([
    record('a', { canonicalCwd: '/one/work/atlas' }),
    record('b', { canonicalCwd: '/two/work/atlas' }),
    record('c', { canonicalCwd: '/two/worktrees/atlas' })
  ]);
  assert.deepEqual(view.projects[0].worktrees.map((group) => group.name).toSorted(),
    ['one/work/atlas', 'two/work/atlas', 'worktrees/atlas']);
  assert.ok(view.projects.every((project) => project.count > 0 && project.worktrees.every((group) => group.count > 0)));
}

// Projects and worktrees are counted from the filtered rows. Rows group by the
// key SQL gave them, so a repository's worktrees sit under it, and activity
// orders every level.
{
  const view = buildSessionHistoryViewModel(rows);
  assert.equal(view.totalCount, 5);
  assert.deepEqual(view.projects.map((project) => [project.name, project.count]), [
    ['atlas', 3],
    ['beacon', 1],
    ['cedar', 1]
  ]);

  const atlas = view.projects[0];
  assert.equal(atlas.singleCheckout, false);
  assert.deepEqual(atlas.worktrees.map((worktree) => [worktree.name, worktree.count]), [
    ['feature-one', 2],
    ['atlas', 1]
  ]);
  assert.deepEqual(atlas.worktrees[0].rows.map((row) => row.record.key), [
    'atlas-lane-new',
    'atlas-lane-old'
  ]);

  const beacon = view.projects.find((project) => project.name === 'beacon')!;
  assert.equal(beacon.singleCheckout, true);
  assert.equal(beacon.worktrees[0].rows[0].displayTitle, 'Trace the first visible message');
  assert.equal(view.projects.find((project) => project.name === 'cedar')!.worktrees[0].rows[0].statusHint, 'Live');
}

// Search covers the displayed content plus project and worktree identity.
{
  assert.equal(buildSessionHistoryViewModel(rows, { query: 'cache invalidation' }).totalCount, 1);
  assert.equal(buildSessionHistoryViewModel(rows, { query: 'feature-one' }).totalCount, 2);
  assert.equal(buildSessionHistoryViewModel(rows, { query: 'beacon' }).totalCount, 1);
  assert.equal(buildSessionHistoryViewModel(rows, { query: 'atlas' }).totalCount, 3);
}

// The folder never decides the group: a session in an unrelated folder stays in
// its project, two projects with one name stay apart, and the Project scope
// keeps only the active group. "No project" comes last, as in the rail, even
// when it holds the newest session.
{
  const records = [
    record('atlas-main'),
    record('atlas-moved', { canonicalCwd: '/tmp/elsewhere' }),
    record('other-atlas', { projectGroupKey: 'project:other', projectGroupLabel: 'atlas' }),
    record('plain', {
      canonicalCwd: '/Users/dev', projectGroupKey: 'none', projectGroupLabel: 'No project', updatedAt: '2026-08-10T12:00:00Z'
    })
  ];
  const view = buildSessionHistoryViewModel(records);
  assert.deepEqual(view.projects.map((project) => [project.key, project.name, project.count]).toSorted(), [
    ['none', 'No project', 1],
    ['project:other', 'atlas', 1],
    ['repo:github.com/dev/atlas', 'atlas', 2]
  ]);
  assert.equal(view.projects.at(-1)!.key, 'none');
  assert.deepEqual(
    filterSessionHistoryRecords(records, { scope: 'project', projectKey: ATLAS.projectGroupKey }).map((row) => row.key),
    ['atlas-main', 'atlas-moved']
  );
  assert.deepEqual(filterSessionHistoryRecords(records, { scope: 'project', projectKey: null }), []);
}

// Provider filtering is exact, keeps a stable provider roster, and recomputes
// every project/worktree badge from only the rows that remain.
{
  const view = buildSessionHistoryViewModel(rows, { provider: 'beta' });
  assert.deepEqual(view.providers.map((provider) => provider.value), ['alpha', 'beta']);
  assert.equal(view.totalCount, 2);
  assert.deepEqual(view.projects.map((project) => [project.name, project.count]), [
    ['beacon', 1],
    ['cedar', 1]
  ]);
}

// A session run below the main checkout is grouped into that checkout, and a
// deleted worktree's historical sessions remain visible beside live checkouts.
{
  const view = buildSessionHistoryViewModel([
    record('nested-main', { canonicalCwd: '/Users/dev/work/atlas/packages/app' }),
    record('deleted-lane', { canonicalCwd: '/Users/dev/work/worktrees/atlas/deleted-lane' })
  ], {
    // Keyed by group. A group no session names adds nothing: its label comes from the sessions.
    checkouts: {
      [ATLAS.projectGroupKey]: [
        { path: '/Users/dev/work/atlas', branch: 'main', isMain: true },
        { path: '/Users/dev/work/worktrees/atlas/live-lane', branch: 'live', isMain: false }
      ],
      'project:unseen': [{ path: '/Users/dev/work/unseen', branch: 'main', isMain: true }]
    }
  });
  assert.equal(view.projects.length, 1);
  assert.deepEqual(view.projects[0].worktrees.map((worktree) => [worktree.name, worktree.count]), [
    ['atlas', 1],
    ['deleted-lane', 1]
  ]);
  assert.equal(view.totalCount, 2);
}

// Groups start closed. Opening a worktree is independent, while closing its
// project still hides every row below it.
{
  const view = buildSessionHistoryViewModel(rows);
  const atlas = view.projects.find((project) => project.name === 'atlas')!;
  const feature = atlas.worktrees.find((worktree) => worktree.name === 'feature-one')!;
  const initial = createSessionHistoryCollapseState();
  const projectOpen = toggleSessionHistoryGroup(initial, 'project', atlas.key);
  const worktreeOpen = toggleSessionHistoryGroup(projectOpen, 'worktree', feature.key);
  assert.equal(isSessionHistoryGroupOpen(initial, 'worktree', feature.key), false);
  assert.equal(isSessionHistoryGroupOpen(worktreeOpen, 'worktree', feature.key), true);
  assert.equal(visibleSessionHistoryRows(view.projects, worktreeOpen).length, 2);

  const projectCollapsed = toggleSessionHistoryGroup(worktreeOpen, 'project', atlas.key);
  assert.equal(isSessionHistoryGroupOpen(projectCollapsed, 'project', atlas.key), false);
  assert.equal(visibleSessionHistoryRows(view.projects, projectCollapsed).length, 0);
  assert.equal(
    isSessionHistoryGroupOpen(toggleSessionHistoryGroup(projectCollapsed, 'project', atlas.key), 'project', atlas.key),
    true
  );
}

console.log('sessionHistoryViewModel: all tests passed');
