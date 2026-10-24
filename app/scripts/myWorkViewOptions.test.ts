import './svelteRuneTestSetup.ts';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { parse } from 'svelte/compiler';
import ts from 'typescript';

import {
  buildMyWorkGroups,
  DEFAULT_MY_WORK_VIEW_OPTIONS,
  matchesMyWorkFilters,
  myWorkGroupSessions,
  myWorkProject,
  normalizeMyWorkFilters,
  normalizeMyWorkViewOptions,
  prepareMyWorkSessions,
  reorderMyWorkSessions,
  type MyWorkGroup
} from '../src/lib/shell/components/myWorkViewOptions.ts';
import type { OwnedSession } from '../src/lib/shell/ownedSessions.ts';
import { ownedSessionMetaForBackend } from '../src/lib/shell/ownedSessions.ts';
import { hydrateOwned, ownedSessionStatusPatch, rail, updateOwnedSession } from '../src/lib/shell/stores/sessionRailStore.svelte.ts';

function session(ownedId: string, extra: Record<string, unknown> = {}): OwnedSession {
  return {
    ownedId,
    title: ownedId,
    projectGroupKey: 'repo:github.com/me/mac-command-bar',
    projectGroupLabel: 'mac-command-bar',
    cwd: '/work/mac-command-bar',
    agent: 'claude',
    executionEnvironment: 'local',
    completedAt: null,
    settledAt: null,
    lastActivity: null,
    ...extra
  } as unknown as OwnedSession;
}

function shape(groups: MyWorkGroup[]): unknown[] {
  return groups.map((group) => [
    group.key,
    group.label,
    group.sessions.map((row) => row.ownedId),
    ...(group.subgroups ? [shape(group.subgroups)] : [])
  ]);
}

// The project group is the one SQL gave the record. A new folder or branch does
// not move a session, because neither takes part.
{
  const home = { key: 'repo:github.com/me/mac-command-bar', label: 'mac-command-bar' };
  assert.deepEqual(myWorkProject(session('one')), home);
  assert.deepEqual(myWorkProject(session('moved', { cwd: '/work/worktrees/other', branch: 'feature' })), home);
  assert.deepEqual(
    myWorkProject(session('plain', { projectGroupKey: 'none', projectGroupLabel: 'No project' })),
    { key: 'none', label: 'No project' }
  );
}

// Recent activity uses the transcript stamp first, then lifecycle stamps, and
// breaks ties by session id when no reliable clock exists. Name sorting is case-insensitive.
{
  const rows = [
    session('missing-a', { title: 'Zulu' }),
    session('older', { lastActivity: '2026-08-08T10:00:00.000Z', title: 'beta' }),
    session('newer', { completedAt: '2026-08-09T10:00:00.000Z', title: 'Alpha' }),
    session('missing-b', { title: 'gamma' })
  ];
  assert.deepEqual(
    prepareMyWorkSessions(rows, DEFAULT_MY_WORK_VIEW_OPTIONS).map((row) => row.ownedId),
    ['newer', 'older', 'missing-a', 'missing-b']
  );
  assert.deepEqual(
    prepareMyWorkSessions(rows, { sortBy: 'name' }).map((row) => row.ownedId),
    ['newer', 'older', 'missing-b', 'missing-a']
  );
}

// TSK-1360: two remote sessions that tie on activity keep their rail order
// across a restart, although the list arrives in a different order after it.
{
  const first = session('b-remote', { executionEnvironment: 'remote', title: 'Same' });
  const second = session('a-remote', { executionEnvironment: 'remote', title: 'Same' });
  for (const options of [DEFAULT_MY_WORK_VIEW_OPTIONS, { sortBy: 'name' as const }, { sortBy: 'manual' as const }]) {
    const beforeRestart = prepareMyWorkSessions([first, second], options).map((row) => row.ownedId);
    const afterRestart = prepareMyWorkSessions([second, first], options).map((row) => row.ownedId);
    assert.deepEqual(afterRestart, beforeRestart, `${options.sortBy} order survives a restart`);
  }
}

// Project sections are keyed by the group key, so two same-named projects stay
// apart; projects read A to Z and "No project" comes last.
{
  const one = { projectGroupKey: 'project:one', projectGroupLabel: 'shared' };
  const groups = buildMyWorkGroups(
    [
      session('working', one),
      session('plain', { projectGroupKey: 'none', projectGroupLabel: 'No project' }),
      session('done', { projectGroupKey: 'project:two', projectGroupLabel: 'shared', completedAt: '2026-08-09T10:00:00.000Z' }),
      session('settled', { ...one, completedAt: '2026-08-08T10:00:00.000Z', settledAt: '2026-08-09T12:00:00.000Z' }),
      session('alpha', { projectGroupKey: 'repo:github.com/me/alpha', projectGroupLabel: 'alpha' })
    ],
    { ...DEFAULT_MY_WORK_VIEW_OPTIONS, groupByProject: true, groupByStatus: false }
  );
  assert.deepEqual(shape(groups), [
    ['repo:github.com/me/alpha', 'alpha', ['alpha']],
    ['project:one', 'shared', ['settled', 'working']],
    ['project:two', 'shared', ['done']],
    ['none', 'No project', ['plain']]
  ]);
}

// Status groups have a stable Working / Done / Settled order and carry counts
// through their session arrays.
{
  const groups = buildMyWorkGroups(
    [
      session('settled', { settledAt: '2026-08-09T12:00:00.000Z' }),
      session('working'),
      session('done', { completedAt: '2026-08-09T10:00:00.000Z' })
    ],
    { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', groupByProject: false, groupByStatus: true }
  );
  assert.deepEqual(groups.map((group) => [group.label, group.sessions.length]), [
    ['Working', 1],
    ['Done', 1],
    ['Settled', 1]
  ]);
}

// buildMyWorkGroups puts Working/Done/Settled outside and splits each by project
// when both groupings are on, so marking a session done moves it down to Done.
// The project sections inside a status use the SQL group key, with "No project" last.
{
  const groups = buildMyWorkGroups(
    [
      session('z-working', { projectGroupKey: 'project:zeta', projectGroupLabel: 'zeta', title: 'a' }),
      session('a-settled', { projectGroupKey: 'project:alpha', projectGroupLabel: 'alpha', title: 'b', settledAt: '2026-08-09T12:00:00.000Z' }),
      session('a-working', { projectGroupKey: 'project:alpha', projectGroupLabel: 'alpha', title: 'c' }),
      session('a-done', { projectGroupKey: 'project:alpha', projectGroupLabel: 'alpha', title: 'd', completedAt: '2026-08-09T10:00:00.000Z' }),
      session('n-working', { projectGroupKey: 'none', projectGroupLabel: 'No project', title: 'e' })
    ],
    { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc', groupByProject: true, groupByStatus: true }
  );
  assert.deepEqual(shape(groups), [
    ['working', 'Working', ['z-working', 'a-working', 'n-working'], [
      ['working::project:alpha', 'alpha', ['a-working']],
      ['working::project:zeta', 'zeta', ['z-working']],
      ['working::none', 'No project', ['n-working']]
    ]],
    ['done', 'Done', ['a-done'], [
      ['done::project:alpha', 'alpha', ['a-done']]
    ]],
    ['settled', 'Settled', ['a-settled'], [
      ['settled::project:alpha', 'alpha', ['a-settled']]
    ]]
  ]);
}

// Pinned sessions leave their own section for a Pinned section at the top,
// whatever the grouping and whatever their status, until they are unpinned.
{
  const rows = [
    session('working', { title: 'a' }),
    session('pinned-done', { title: 'b', completedAt: '2026-08-09T10:00:00.000Z', pinnedAt: '2026-10-07T10:00:00.000Z' }),
    session('pinned-working', { title: 'c', pinnedAt: '2026-10-07T11:00:00.000Z' })
  ];
  const byProject = buildMyWorkGroups(rows, { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc' });
  assert.deepEqual(shape(byProject), [
    ['pinned', 'Pinned', ['pinned-done', 'pinned-working']],
    ['repo:github.com/me/mac-command-bar', 'mac-command-bar', ['working']]
  ]);
  const byStatus = buildMyWorkGroups(rows, {
    ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc', groupByProject: false, groupByStatus: true
  });
  assert.deepEqual(shape(byStatus), [
    ['pinned', 'Pinned', ['pinned-done', 'pinned-working']],
    ['working', 'Working', ['working']]
  ]);
  const ungrouped = buildMyWorkGroups(rows, {
    ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc', groupByProject: false, groupByStatus: false
  });
  assert.deepEqual(shape(ungrouped), [
    ['pinned', 'Pinned', ['pinned-done', 'pinned-working']],
    ['all', '', ['working']]
  ]);
}

// designated_orchestrator_is_pinned_first: the session the owner marked sits alone
// above everything, Pinned included. If two machines still carry a mark, the
// newest wins and the older one stays in its own group.
{
  const rows = [
    session('working', { title: 'a' }),
    session('pinned', { title: 'b', pinnedAt: '2026-10-07T10:00:00.000Z' }),
    session('old-mark', { title: 'c', orchestratorMarkedAtMs: 1 }),
    session('main', { title: 'd', pinnedAt: '2026-10-07T11:00:00.000Z', orchestratorMarkedAtMs: 2 })
  ];
  assert.deepEqual(shape(buildMyWorkGroups(rows, { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc' })), [
    ['orchestrator', 'Main orchestrator', ['main']],
    ['pinned', 'Pinned', ['pinned']],
    ['repo:github.com/me/mac-command-bar', 'mac-command-bar', ['working', 'old-mark']]
  ]);
}

// helpers_group_under_parent_once: a helper is drawn under its parent's row and
// nowhere else, the orchestrator's helpers included. A helper whose parent is not
// in the list stays an ordinary row.
{
  const rows = [
    session('parent', { title: 'a' }),
    session('helper', { title: 'b', helperOf: 'parent' }),
    session('main', { title: 'c', orchestratorMarkedAtMs: 1 }),
    session('main-helper', { title: 'd', helperOf: 'main' }),
    session('orphan', { title: 'e', helperOf: 'gone' })
  ];
  const groups = buildMyWorkGroups(rows, { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc' });
  assert.deepEqual(shape(groups), [
    ['orchestrator', 'Main orchestrator', ['main']],
    ['repo:github.com/me/mac-command-bar', 'mac-command-bar', ['parent', 'orphan']]
  ]);
  assert.deepEqual(groups[0].helpers?.main?.map((row) => row.ownedId), ['main-helper']);
  assert.deepEqual(groups[1].helpers?.parent?.map((row) => row.ownedId), ['helper']);
  assert.deepEqual(groups.flatMap(myWorkGroupSessions).map((row) => row.ownedId).sort(),
    ['helper', 'main', 'main-helper', 'orphan', 'parent']);
}

// finished_helper_leaves_parent_when_grouped_by_status: with status groups on, a Done or
// Settled helper sits in its own status group, not under its working parent; a working
// helper stays under the parent, and with status groups off every helper stays nested.
{
  const rows = [
    session('parent', { title: 'p' }),
    session('busy', { title: 'b', helperOf: 'parent' }),
    session('done', { title: 'd', helperOf: 'parent', completedAt: '2026-08-09T10:00:00.000Z' }),
    session('settled', { title: 's', helperOf: 'parent', completedAt: '2026-08-08T10:00:00.000Z', settledAt: '2026-08-09T12:00:00.000Z' })
  ];
  const byStatus = buildMyWorkGroups(rows, { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc', groupByProject: false, groupByStatus: true });
  assert.deepEqual(shape(byStatus), [['working', 'Working', ['parent']], ['done', 'Done', ['done']], ['settled', 'Settled', ['settled']]]);
  assert.deepEqual(byStatus[0].helpers?.parent?.map((row) => row.ownedId), ['busy']);
  const plain = buildMyWorkGroups(rows, { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc' });
  assert.deepEqual(plain[0].helpers?.parent?.map((row) => row.ownedId), ['busy', 'done', 'settled']);
}

// workers_group_under_machine_and_run_once: each run's workers form one section,
// named for the run, after the project sections; a worker is in no other section.
{
  const rows = [
    session('lane', { title: 'a' }),
    session('build', { title: 'b', runId: 'run-1', runLabel: 'TSK-9 · Fix and review' }),
    session('review', { title: 'c', runId: 'run-1', runLabel: 'TSK-9 · Fix and review' }),
    session('remote-build', { title: 'd', runId: 'run-2', runLabel: 'TSK-10 · Ship',
      executionEnvironment: 'remote', remoteProfileId: 'workbox-test' })
  ];
  assert.deepEqual(shape(buildMyWorkGroups(rows, { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'name', sortDirection: 'asc' })), [
    ['repo:github.com/me/mac-command-bar', 'mac-command-bar', ['lane']],
    ['run:run-1', 'TSK-9 · Fix and review', ['build', 'review']],
    ['run:run-2', 'TSK-10 · Ship', ['remote-build']]
  ]);
}

// pinned_worker_sits_in_pinned: pinning a workflow worker moves it out of its run's section.
{
  const rows = [
    session('build', { title: 'b', runId: 'run-1', runLabel: 'TSK-9 · Fix and review' }),
    session('review', { title: 'c', runId: 'run-1', runLabel: 'TSK-9 · Fix and review', pinnedAt: 5 })
  ];
  assert.deepEqual(shape(buildMyWorkGroups(rows, DEFAULT_MY_WORK_VIEW_OPTIONS)), [
    ['pinned', 'Pinned', ['review']],
    ['run:run-1', 'TSK-9 · Fix and review', ['build']]
  ]);
}

// pinned_helper_sits_in_pinned: pinning a helper moves it out from under its parent.
{
  const rows = [session('parent', { title: 'p' }), session('helper', { title: 'h', helperOf: 'parent', pinnedAt: 5 })];
  assert.deepEqual(shape(buildMyWorkGroups(rows, DEFAULT_MY_WORK_VIEW_OPTIONS)), [
    ['pinned', 'Pinned', ['helper']],
    ['repo:github.com/me/mac-command-bar', 'mac-command-bar', ['parent']]
  ]);
}

// child_needing_owner_marks_group: a helper waiting on the owner counts in its
// parent's section, so the section's "needs you" mark lifts.
{
  const rows = [session('parent'), session('helper', { helperOf: 'parent', pendingPermission: true })];
  const [group] = buildMyWorkGroups(rows, DEFAULT_MY_WORK_VIEW_OPTIONS);
  assert.deepEqual(myWorkGroupSessions(group).filter((row) => (row as unknown as { pendingPermission?: boolean }).pendingPermission)
    .map((row) => row.ownedId), ['helper']);
}

// renamed_session_is_not_pinned: a title never makes an orchestrator.
{
  const rows = [session('named', { title: 'orchestrator' })];
  assert.deepEqual(shape(buildMyWorkGroups(rows, DEFAULT_MY_WORK_VIEW_OPTIONS)).map((group) => (group as unknown[])[0]),
    ['repo:github.com/me/mac-command-bar']);
}

// Custom order follows the saved order inside each group; a session with no
// saved place yet goes to the top, newest first.
{
  const rows = [
    session('a', { lastActivity: '2026-08-01T10:00:00.000Z' }),
    session('b', { lastActivity: '2026-08-02T10:00:00.000Z' }),
    session('c', { lastActivity: '2026-08-03T10:00:00.000Z', completedAt: '2026-08-03T10:00:00.000Z' }),
    session('new-old', { lastActivity: '2026-08-04T10:00:00.000Z' }),
    session('new-new', { lastActivity: '2026-08-05T10:00:00.000Z' })
  ];
  const groups = buildMyWorkGroups(
    rows,
    { ...DEFAULT_MY_WORK_VIEW_OPTIONS, sortBy: 'manual', groupByProject: false, groupByStatus: true },
    ['c', 'a', 'b']
  );
  assert.deepEqual(shape(groups), [
    ['working', 'Working', ['new-new', 'new-old', 'a', 'b']],
    ['done', 'Done', ['c']]
  ]);
  assert.equal(normalizeMyWorkViewOptions({ sortBy: 'manual' }).sortBy, 'manual');
}

// Moving a row reorders its own group within that group's existing places,
// so the other groups keep theirs.
{
  const seeded = ['w1', 'w2', 'd1', 'd2', 's1'];
  assert.deepEqual(reorderMyWorkSessions(seeded, ['d1', 'd2'], 'd2', 'd1', 'before'), ['w1', 'w2', 'd2', 'd1', 's1']);
  // A place saved for a session no longer shown is kept.
  assert.deepEqual(reorderMyWorkSessions(['gone', 'a', 'x', 'b'], ['a', 'b'], 'b', 'a', 'before'), ['gone', 'b', 'x', 'a']);
  // Group sessions with no saved place yet are added at the end, in drawn order.
  assert.deepEqual(reorderMyWorkSessions(['x'], ['a', 'b', 'c'], 'a', 'c', 'after'), ['x', 'b', 'c', 'a']);
  // Nothing changes when the dragged or target row is not in the group.
  assert.deepEqual(reorderMyWorkSessions(seeded, ['d1', 'd2'], 'w1', 'd1', 'before'), seeded);
  assert.deepEqual(reorderMyWorkSessions(seeded, ['d1', 'd2'], 'd1', 'w1', 'before'), seeded);
  assert.deepEqual(reorderMyWorkSessions(seeded, ['d1', 'd2'], 'd1', 'd1', 'after'), seeded);
}

// buildMyWorkGroups with neither grouping returns one unlabeled group.
{
  const groups = buildMyWorkGroups(
    [session('one'), session('two', { settledAt: '2026-08-09T12:00:00.000Z' })],
    { ...DEFAULT_MY_WORK_VIEW_OPTIONS, groupByProject: false, groupByStatus: false }
  );
  assert.deepEqual(shape(groups), [['all', '', ['two', 'one']]]);
}

// normalizeMyWorkViewOptions loads an old single-value groupBy.
{
  const grouping = (value: unknown) => {
    const options = normalizeMyWorkViewOptions(value);
    return [options.groupByProject, options.groupByStatus];
  };
  assert.deepEqual(grouping({ groupBy: 'project' }), [true, false]);
  assert.deepEqual(grouping({ groupBy: 'status' }), [false, true]);
  assert.deepEqual(grouping({ groupBy: 'none' }), [false, false]);
  // Nothing saved: a new install groups by project. A saved choice keeps its own values.
  assert.deepEqual(grouping({ groupBy: 'wrong' }), [true, false]);
  assert.deepEqual(grouping({}), [true, false]);
  assert.deepEqual(grouping({ groupByProject: false, groupByStatus: true }), [false, true]);
  assert.deepEqual(grouping({ groupBy: 'status', groupByProject: true, groupByStatus: false }), [true, false]);
  assert.deepEqual(normalizeMyWorkViewOptions({ groupBy: 'wrong', sortBy: 'name' }), {
    groupByProject: true,
    groupByStatus: false,
    sortBy: 'name',
    sortDirection: 'asc'
  });
  assert.deepEqual(normalizeMyWorkViewOptions(null), DEFAULT_MY_WORK_VIEW_OPTIONS);
}

// normalizeMyWorkViewOptions drops an old visibleStatuses.
{
  const options = normalizeMyWorkViewOptions({ groupBy: 'project', sortBy: 'recent', visibleStatuses: [] });
  assert.equal('visibleStatuses' in options, false);
  const rows = [
    session('working'),
    session('done', { completedAt: '2026-08-09T10:00:00.000Z' }),
    session('settled', { settledAt: '2026-08-09T12:00:00.000Z' })
  ];
  assert.deepEqual(
    prepareMyWorkSessions(rows, { ...options, sortBy: 'name', sortDirection: 'asc' }).map((row) => row.ownedId),
    ['done', 'settled', 'working']
  );
}

// matchesMyWorkFilters: OR inside a group, AND across groups, empty group = no filter.
{
  const rows = [
    session('claude-local-working', { agent: 'claude' }),
    session('codex-remote-done', { agent: 'codex', executionEnvironment: 'remote', completedAt: '2026-08-09T10:00:00.000Z' }),
    session('agy-local-settled', { agent: 'antigravity', settledAt: '2026-08-09T12:00:00.000Z' })
  ];
  const kept = (filters: Record<string, string[]>) =>
    rows.filter((row) => matchesMyWorkFilters(row, filters)).map((row) => row.ownedId);
  assert.deepEqual(kept({}), ['claude-local-working', 'codex-remote-done', 'agy-local-settled']);
  assert.deepEqual(kept({ provider: [], status: [], location: [] }), [
    'claude-local-working',
    'codex-remote-done',
    'agy-local-settled'
  ]);
  assert.deepEqual(kept({ provider: ['claude', 'codex'] }), ['claude-local-working', 'codex-remote-done']);
  assert.deepEqual(kept({ provider: ['claude', 'codex'], location: ['local'] }), ['claude-local-working']);
  assert.deepEqual(kept({ status: ['done', 'settled'] }), ['codex-remote-done', 'agy-local-settled']);
  assert.deepEqual(kept({ provider: ['antigravity'], status: ['working'] }), []);
}

// Run the route's status callbacks through the real rail projection and filter.
{
  const route = process.env.STATUS_TEST_BASE
    ? execFileSync('git', ['show', `${process.env.STATUS_TEST_BASE}:tauri-svelte-preview/src/routes/+page.svelte`], { encoding: 'utf8' })
    : readFileSync(new URL('../src/routes/+page.svelte', import.meta.url), 'utf8');
  const ast = parse(route, { modern: true });
  // Every row change goes through the route's one save path: its per-row write
  // chain and the two functions that use it.
  type MetaWrite = { ownedId: string; meta: { completedAt: string | null; settledAt: string | null; title: string | null; pinnedAt: string | null } };
  const routeCode = ts.transpileModule(['sessionSaves', 'changeSessionStatus', 'saveSessionChange'].map((name) => {
    const node = ast.instance?.content.body.find((candidate) =>
      (candidate.type === 'FunctionDeclaration' && candidate.id?.name === name)
      || (candidate.type === 'VariableDeclaration' && candidate.declarations[0]?.id.type === 'Identifier'
        && candidate.declarations[0].id.name === name));
    assert.ok(node, `route ${name} exists`);
    return route.slice(node.start, node.end);
  }).join('\n'), { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  const loadRouteSaves = (writeMeta: (input: MetaWrite) => Promise<void>) => new Function('selection',
    'ownedSessionStatusPatch', 'updateOwnedSession', 'ownedSessionMetaForBackend',
    'updateAgentConversationSessionMetaFromTauri',
    `${routeCode}; return { changeSessionStatus, saveSessionChange };`)(
      { get railOwned() { return rail.owned; } }, ownedSessionStatusPatch, updateOwnedSession,
      ownedSessionMetaForBackend, writeMeta
    ) as {
      changeSessionStatus(ownedId: string, status: string): Promise<void>;
      saveSessionChange(ownedId: string, patch: Partial<OwnedSession>): Promise<void>;
    };
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

  // Writes for one row go out one at a time, each sending the row as it is
  // then, so a rename followed by a pin keeps both; a failed older rename does
  // not put back a title over a newer one.
  {
    const sent: MetaWrite[] = [];
    const pending: Array<{ resolve(): void; reject(error: Error): void }> = [];
    const { saveSessionChange } = loadRouteSaves((input) => {
      sent.push(input);
      return new Promise<void>((resolve, reject) => pending.push({ resolve, reject }));
    });
    hydrateOwned([session('row', { title: 'A' })]);
    const rename = saveSessionChange('row', { title: 'B' });
    const pin = saveSessionChange('row', { pinnedAt: '2026-10-07T10:00:00.000Z' });
    await settle();
    assert.equal(sent.length, 1, 'the pin waits for the rename');
    pending[0].resolve();
    await rename;
    await settle();
    assert.deepEqual([sent[1].meta.title, sent[1].meta.pinnedAt], ['B', '2026-10-07T10:00:00.000Z']);
    pending[1].resolve();
    await pin;
    assert.deepEqual([rail.owned[0].title, rail.owned[0].pinnedAt], ['B', '2026-10-07T10:00:00.000Z']);

    const older = saveSessionChange('row', { title: 'C' });
    const newer = saveSessionChange('row', { title: 'D' });
    await settle();
    pending[2].reject(new Error('refused'));
    await older;
    assert.equal(rail.owned[0].title, 'D', 'a failed older rename leaves the newer title');
    await settle();
    assert.equal(sent[3].meta.title, 'D');
    pending[3].resolve();
    await newer;
  }

  const saves: Array<{ ownedId: string; completedAt: string | null; settledAt: string | null }> = [];
  const { changeSessionStatus } = loadRouteSaves(async ({ ownedId, meta }) => {
    saves.push({ ownedId, completedAt: meta.completedAt, settledAt: meta.settledAt });
  });
  let column: { attributes: Array<{ name?: string; value?: { expression?: { start: number; end: number } } }> } | undefined;
  function findColumn(node: unknown): void {
    if (!node || typeof node !== 'object') return;
    const current = node as { type?: string; name?: string; [key: string]: unknown };
    if (current.type === 'Component' && current.name === 'SessionsColumn') column = current as typeof column;
    for (const value of Object.values(current)) {
      if (Array.isArray(value)) value.forEach(findColumn);
      else if (value && typeof value === 'object') findColumn(value);
    }
  }
  findColumn(ast.fragment);
  assert.ok(column, 'route renders the sessions column');
  const invoke = async (name: string, ownedId: string) => {
    const expression = column.attributes.find((attribute) => attribute.name === name)?.value?.expression;
    assert.ok(expression, `${name} status callback exists`);
    const callback = new Function('changeSessionStatus', `return (${route.slice(expression.start, expression.end)});`)(
      changeSessionStatus
    ) as (id: string) => void;
    callback(ownedId);
    await settle();
  };
  hydrateOwned([session('working'), session('done'), session('settled')]);
  await invoke('onComplete', 'done');
  await invoke('onSettle', 'settled');
  const kept = (status: string[]) => rail.owned
    .filter((row) => matchesMyWorkFilters(row, { status }))
    .map((row) => row.ownedId);
  assert.deepEqual(kept(['working']), ['working']);
  assert.deepEqual(kept(['done']), ['done']);
  assert.deepEqual(kept(['settled']), ['settled']);
  assert.deepEqual(kept(['done', 'settled']), ['done', 'settled']);
  assert.deepEqual(saves.map(({ ownedId, completedAt, settledAt }) => [ownedId, !!completedAt, !!settledAt]), [
    ['done', true, false], ['settled', true, true]
  ]);
  await invoke('onUnsettle', 'settled');
  assert.deepEqual(kept(['done']), ['done', 'settled']);
  await invoke('onReopen', 'done');
  assert.deepEqual(kept(['working']), ['working', 'done']);
  assert.deepEqual(saves.map(({ ownedId, completedAt, settledAt }) => [ownedId, !!completedAt, !!settledAt]), [
    ['done', true, false], ['settled', true, true], ['settled', true, false], ['done', false, false]
  ]);
}

// normalizeMyWorkFilters keeps only known group ids and option values.
{
  assert.deepEqual(normalizeMyWorkFilters(null), { provider: [], status: [], location: [] });
  assert.deepEqual(normalizeMyWorkFilters('junk'), { provider: [], status: [], location: [] });
  assert.deepEqual(
    normalizeMyWorkFilters({
      provider: ['codex', 'bogus', 'claude'],
      status: 'working',
      location: ['remote'],
      extra: ['x']
    }),
    { provider: ['claude', 'codex'], status: [], location: ['remote'] }
  );
}

console.log('myWorkViewOptions: grouping, sorting, filters, project identity, and normalization passed');
