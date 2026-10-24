// Regression pins for the session rail (TSK-1167): each test names the
// behaviour it holds in place and, where there is one, the commit that fixed it.
import './svelteRuneTestSetup.ts';
import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  buildMyWorkGroups,
  DEFAULT_MY_WORK_VIEW_OPTIONS,
  prepareMyWorkSessions
} from '../src/lib/shell/components/myWorkViewOptions.ts';
import type { OwnedSession } from '../src/lib/shell/ownedSessions.ts';
import { ownedSessionFromBackend, ownedSessionMetaForBackend } from '../src/lib/shell/ownedSessions.ts';
import { sessionLabel, sessionTitleFromPrompt, stripCells } from '../src/lib/shell/sessionStrip.ts';
import {
  hydrateOwned,
  ownedSessionStatusPatch,
  rail,
  removeOwnedSession,
  setActiveOwned,
  setAvailable,
  updateOwnedSession
} from '../src/lib/shell/stores/sessionRailStore.svelte.ts';

function session(ownedId: string, extra: Record<string, unknown> = {}): OwnedSession {
  return {
    ownedId,
    title: ownedId,
    projectGroupKey: 'repo:github.com/me/app',
    projectGroupLabel: 'app',
    cwd: '/work/app',
    agent: 'claude',
    executionEnvironment: 'local',
    completedAt: null,
    settledAt: null,
    lastActivity: null,
    ...extra
  } as unknown as OwnedSession;
}

const ids = (rows: readonly OwnedSession[]) => rows.map((row) => row.ownedId);

test('a reconnect that resends equal background work as new objects does not re-render the rail (854bbfae2)', () => {
  hydrateOwned([session('row', { backgroundWork: [{ id: 't1', kind: 'command', label: 'Build', startedAtMs: 1 }] })]);
  const before = rail.owned;
  updateOwnedSession('row', { backgroundWork: [{ id: 't1', kind: 'command', label: 'Build', startedAtMs: 1 }] });
  assert.equal(rail.owned, before, 'an equal copy of the list is no change');
  updateOwnedSession('row', { backgroundWork: [{ id: 't1', kind: 'command', label: 'Build', startedAtMs: 2 }] });
  assert.notEqual(rail.owned, before, 'a changed item is a change');
});

test('a row update never changes the row id it was sent for', () => {
  hydrateOwned([session('keep'), session('other')]);
  updateOwnedSession('keep', { ownedId: 'stolen', title: 'Renamed' });
  assert.deepEqual(rail.owned.map((row) => [row.ownedId, row.title]), [['keep', 'Renamed'], ['other', 'other']]);
});

test('removing a row clears the selection only when that row was selected', () => {
  hydrateOwned([session('a'), session('b')]);
  setActiveOwned('a');
  removeOwnedSession('b');
  assert.equal(rail.activeOwnedId, 'a');
  assert.deepEqual(ids(rail.owned), ['a']);
  removeOwnedSession('a');
  assert.equal(rail.activeOwnedId, null);
});

test('the rail keeps no History turns for the sessions it can resume (d6435bb3a)', () => {
  setAvailable([{ id: 'native-1', title: 'Old', latestTurns: [{ speaker: 'agent', text: 'Finished.' }] }] as never);
  assert.equal(rail.available[0].latestTurns, undefined);
  assert.equal(rail.available[0].title, 'Old', 'the row metadata stays');
});

test('status changes keep the first done time and reopen clears both stamps (afa45ca9a)', () => {
  const first = new Date('2026-10-01T10:00:00.000Z');
  const later = new Date('2026-10-02T10:00:00.000Z');
  const done = ownedSessionStatusPatch({ completedAt: null }, 'done', first);
  assert.deepEqual(done, { completedAt: first.toISOString(), settledAt: null });
  // Marking done again, or archiving (settling) a done row, keeps when it was finished.
  assert.deepEqual(ownedSessionStatusPatch(done, 'done', later), done);
  assert.deepEqual(ownedSessionStatusPatch(done, 'settled', later),
    { completedAt: first.toISOString(), settledAt: later.toISOString() });
  // Archiving a working row marks it done too, so un-archiving lands it in Done.
  assert.deepEqual(ownedSessionStatusPatch({ completedAt: null }, 'settled', later),
    { completedAt: later.toISOString(), settledAt: later.toISOString() });
  assert.deepEqual(ownedSessionStatusPatch(done, 'working', later), { completedAt: null, settledAt: null });
});

test('saving any row change sends the done, archived and pinned stamps back unchanged (0d12439cc)', () => {
  // The backend replaces the stored metadata with what is sent, so a rename that
  // dropped a stamp would unpin, reopen or un-archive the session.
  const record = {
    ownedId: 'owned', provider: 'codex', cwd: '/work/app', state: 'idle', suspended: false,
    createdAtMs: 1, lastActivityAtMs: 2, activeTurnId: null, pendingPermission: false, pendingInput: false,
    title: 'Renamed', completedAt: '2026-10-01T10:00:00.000Z', settledAt: '2026-10-02T10:00:00.000Z',
    pinnedAt: '2026-10-03T10:00:00.000Z', viaCmux: false, resumeCommand: null, ptySessionId: null,
    branch: 'tsk-1-x', taskId: null, pullRequest: null, messageCount: null, latestTurnPreview: null,
    scannedLastActivity: null, nativeSessionId: 'native', model: null
  };
  const meta = ownedSessionMetaForBackend(ownedSessionFromBackend(record as never));
  assert.deepEqual([meta.title, meta.completedAt, meta.settledAt, meta.pinnedAt, meta.branch],
    ['Renamed', record.completedAt, record.settledAt, record.pinnedAt, 'tsk-1-x']);
  assert.equal(ownedSessionMetaForBackend(ownedSessionFromBackend({ ...record, pinnedAt: undefined } as never)).pinnedAt, null);
});

test('reversing a sort keeps tied rows in the same id order (38711d55c)', () => {
  const rows = [session('b', { title: 'Same' }), session('a', { title: 'Same' }), session('c', { title: 'Other' })];
  for (const sortBy of ['recent', 'name'] as const) {
    for (const sortDirection of ['asc', 'desc'] as const) {
      const sorted = ids(prepareMyWorkSessions(rows, { sortBy, sortDirection }));
      const tied = sorted.filter((id) => id !== 'c');
      const message = `${sortBy} ${sortDirection}`;
      assert.deepEqual(tied, ['a', 'b'], message);
      assert.deepEqual(ids(prepareMyWorkSessions([...rows].reverse(), { sortBy, sortDirection })), sorted, message);
    }
  }
  assert.deepEqual(ids(prepareMyWorkSessions(rows, { sortBy: 'name', sortDirection: 'desc' })), ['a', 'b', 'c']);
});

test('pinned rows inside the Pinned section follow the sort in force', () => {
  const rows = [
    session('old-pin', { lastActivity: '2026-10-01T10:00:00.000Z', pinnedAt: '2026-10-05T10:00:00.000Z' }),
    session('new-pin', { lastActivity: '2026-10-03T10:00:00.000Z', pinnedAt: '2026-10-04T10:00:00.000Z' })
  ];
  const [pinned] = buildMyWorkGroups(rows, DEFAULT_MY_WORK_VIEW_OPTIONS);
  assert.deepEqual([pinned.key, ids(pinned.sessions)], ['pinned', ['new-pin', 'old-pin']]);
});

test('an untitled row is called by the front of its id', () => {
  assert.equal(sessionLabel({ title: '', ownedId: '0123456789abcdef' }), '01234567');
  assert.equal(sessionLabel({ title: 'Named', ownedId: '0123456789abcdef' }), 'Named');
});

test('the live title from a first prompt follows the backend rule: first line, trimmed, 64 characters', () => {
  assert.equal(sessionTitleFromPrompt('  Fix the rail  \nmore detail'), 'Fix the rail');
  assert.equal(sessionTitleFromPrompt('line one\r\nline two'), 'line one');
  assert.equal(sessionTitleFromPrompt('   \nsecond line'), null);
  // Capped by characters, not UTF-16 units, so an emoji at the edge is never cut in half.
  const title = sessionTitleFromPrompt(`${'a'.repeat(63)}😀😀`);
  assert.equal(title, `${'a'.repeat(63)}😀`);
});

test('the folded strip lists working rows in rail order, then done rows newest-finished first', () => {
  const cells = stripCells([
    session('done-old', { completedAt: '2026-10-01T10:00:00.000Z' }),
    session('work-b'),
    session('done-new', { completedAt: '2026-10-03T10:00:00.000Z' }),
    session('work-a')
  ], 'done-old');
  assert.deepEqual(cells.map((cell) => [cell.ownedId, cell.done, cell.active]), [
    ['work-b', false, false], ['work-a', false, false], ['done-new', true, false], ['done-old', true, true]
  ]);
});
