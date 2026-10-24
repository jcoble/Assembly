/**
 * worktreeManager.test.mjs — the pure joins and wordings behind the worktree
 * manager pane, run in plain node.
 *
 * Everything under test lives in `worktreeManagerRows.ts`, which is an ordinary
 * TypeScript module (no runes, no DOM, no backend), so node can run it with its
 * own type stripping — the same way `worktreeSafety.test.mjs` does.
 *
 * What is deliberately NOT tested here: the store (a value bag), the service
 * (every line of it is a backend call) and the components. What IS tested is
 * every decision the pane makes about the data: which repository summary
 * belongs to which worktree, which sessions worked in it, what its chips say,
 * whether a plain remove would be refused and why, and exactly what sentence a
 * forced remove shows before it destroys anything.
 */
import assert from 'node:assert/strict';

import {
  buildWorktreeManagerRows,
  describeAheadBehind,
  describeForcedRemoval,
  describeRemovalQuestion,
  describeSessions,
  describeWorktreeAge,
  filterWorktreeRows,
  findAheadBehind,
  normalizeWorktreePath,
  otherFolderGoneBranches,
  primaryCheckoutPath,
  safeRemoveBlockedReason,
  sessionsForWorktree,
  summarizeWorktreeManager,
  worktreeChips,
  worktreeFacts,
  worktreeFolderName
} from '../src/lib/shell/worktrees/worktreeManagerRows.ts';

/** A worktree record shaped like the backend's, with everything clean by default. */
function worktree(overrides = {}) {
  return {
    repo: 'mac-command-bar',
    path: '/Users/me/dev/worktrees/mac-command-bar/tsk-12-thing',
    branch: 'tsk-12-thing',
    taskID: null,
    isDirty: false,
    hasUnmergedCommits: false,
    isPrunable: false,
    prunableReason: null,
    isLocked: false,
    lockedReason: null,
    lastActivity: null,
    deleteEligibility: 'safe',
    ...overrides
  };
}

/** A repository summary shaped like the backend's. */
function summary(overrides = {}) {
  return {
    projectID: 'mac-command-bar',
    projectName: 'mac-command-bar',
    repo: 'mac-command-bar',
    path: '/Users/me/dev/worktrees/mac-command-bar/tsk-12-thing',
    rootLabel: 'tsk-12-thing',
    branch: 'tsk-12-thing',
    taskID: null,
    isWorktree: true,
    isDirty: false,
    stagedCount: 0,
    unstagedCount: 0,
    untrackedCount: 0,
    dirtyCount: 0,
    ahead: 0,
    behind: 0,
    hasUpstream: true,
    lastCommitSha: null,
    lastCommitSubject: null,
    lastCommitAt: null,
    dirtySinceEpochMs: null,
    dirtyStatusFingerprint: '',
    error: null,
    ...overrides
  };
}

/** A session in the shape the pane needs to see one. */
function session(overrides = {}) {
  return {
    ownedId: 'owned-1',
    title: 'Fix the thing',
    cwd: '/Users/me/dev/worktrees/mac-command-bar/tsk-12-thing',
    projectPath: null,
    state: 'background',
    completedAt: null,
    ...overrides
  };
}

const PRIMARY = '/Users/me/dev/work/mac-command-bar';
const NOW = new Date('2026-07-29T12:00:00Z');

// ── paths ────────────────────────────────────────────────────────────────────
// Two spellings of the same folder have to join, or every worktree loses its
// ahead/behind counts and its sessions to a trailing slash.
{
  assert.equal(normalizeWorktreePath('/a/b/'), '/a/b');
  assert.equal(normalizeWorktreePath('  /a/b  '), '/a/b');
  assert.equal(normalizeWorktreePath('/a/b'), '/a/b');
  assert.equal(normalizeWorktreePath('/'), '/');
  assert.equal(normalizeWorktreePath(''), '');
  assert.equal(normalizeWorktreePath(null), '');

  assert.equal(worktreeFolderName('/a/b/tsk-12-thing/'), 'tsk-12-thing');
  assert.equal(worktreeFolderName('/a/b/tsk-12-thing'), 'tsk-12-thing');
  assert.equal(worktreeFolderName(''), '');
}

// The main checkout is the first thing `git worktree list` prints, always. With
// no worktrees at all the folder we asked about is the best answer there is.
{
  const list = [worktree({ path: PRIMARY, branch: 'main' }), worktree()];
  assert.equal(primaryCheckoutPath(list, '/somewhere/else'), PRIMARY);
  assert.equal(primaryCheckoutPath([], PRIMARY), PRIMARY);
  assert.equal(primaryCheckoutPath([], null), '');
}

// ── ahead / behind, joined from the repository summaries ─────────────────────
{
  const tree = worktree();
  const summaries = [
    summary({ path: `${tree.path}/`, ahead: 2, behind: 1 }),
    summary({ path: '/somewhere/else', ahead: 9, behind: 9 })
  ];
  assert.deepEqual(findAheadBehind(tree, summaries), {
    ahead: 2,
    behind: 1,
    hasUpstream: true
  });

  // No summary for this folder: we do not know, and saying "0 ahead" would be a
  // guess dressed up as a fact.
  assert.equal(findAheadBehind(tree, []), null);
  // A summary git could not read is not an answer either.
  assert.equal(findAheadBehind(tree, [summary({ error: 'not a repository' })]), null);
}

{
  assert.equal(
    describeAheadBehind({ ahead: 0, behind: 0, hasUpstream: true }),
    'Up to date with its remote'
  );
  assert.equal(
    describeAheadBehind({ ahead: 1, behind: 0, hasUpstream: true }),
    '1 commit to push'
  );
  assert.equal(
    describeAheadBehind({ ahead: 3, behind: 0, hasUpstream: true }),
    '3 commits to push'
  );
  assert.equal(
    describeAheadBehind({ ahead: 0, behind: 2, hasUpstream: true }),
    '2 commits to pull'
  );
  assert.equal(
    describeAheadBehind({ ahead: 2, behind: 1, hasUpstream: true }),
    '2 commits to push, 1 commit to pull'
  );
  assert.equal(
    describeAheadBehind({ ahead: 0, behind: 0, hasUpstream: false }),
    'This branch is not on any remote'
  );
  assert.equal(describeAheadBehind(null), '');
}

// ── which sessions worked here ───────────────────────────────────────────────
{
  const tree = worktree();
  const sessions = [
    session({ ownedId: 'a', cwd: `${tree.path}/` }),
    session({ ownedId: 'b', cwd: '', projectPath: tree.path, state: 'live' }),
    session({ ownedId: 'c', cwd: '/some/other/folder' }),
    session({ ownedId: 'd', cwd: '', projectPath: null })
  ];
  const links = sessionsForWorktree(tree, sessions);
  assert.deepEqual(
    links.map((link) => link.ownedId),
    ['a', 'b']
  );
  assert.equal(links[0].isRunning, false);
  assert.equal(links[1].isRunning, true);
  assert.equal(links[0].title, 'Fix the thing');

  // A session listed twice is one session.
  const twice = sessionsForWorktree(tree, [session({ ownedId: 'a' }), session({ ownedId: 'a' })]);
  assert.equal(twice.length, 1);
}

{
  assert.equal(describeSessions([]), 'No session has worked here');
  assert.equal(
    describeSessions([{ ownedId: 'a', title: 'x', isRunning: false }]),
    '1 session worked here'
  );
  assert.equal(
    describeSessions([
      { ownedId: 'a', title: 'x', isRunning: true },
      { ownedId: 'b', title: 'y', isRunning: false }
    ]),
    '2 sessions worked here, 1 still running'
  );
  assert.equal(
    describeSessions([
      { ownedId: 'a', title: 'x', isRunning: true },
      { ownedId: 'b', title: 'y', isRunning: true }
    ]),
    '2 sessions worked here, both still running'
  );
}

// ── age ──────────────────────────────────────────────────────────────────────
{
  assert.equal(describeWorktreeAge('2026-07-29T09:00:00Z', NOW), '3h ago');
  assert.equal(describeWorktreeAge(null, NOW), 'no activity recorded');
  assert.equal(describeWorktreeAge('not a date', NOW), 'no activity recorded');
}

// ── chips ────────────────────────────────────────────────────────────────────
// A clean worktree wears nothing. Everything else says what it is in a word a
// person can act on.
{
  assert.deepEqual(worktreeChips(worktree()), []);

  const chipLabels = (overrides) => worktreeChips(worktree(overrides)).map((chip) => chip.label);
  assert.deepEqual(chipLabels({ isDirty: true }), ['Uncommitted changes']);
  assert.deepEqual(chipLabels({ hasUnmergedCommits: true }), ['Commits not pushed']);
  assert.deepEqual(chipLabels({ isLocked: true }), ['Locked']);
  assert.deepEqual(chipLabels({ isPrunable: true }), ['Folder is gone']);
  assert.deepEqual(chipLabels({ isDirty: true, hasUnmergedCommits: true, isLocked: true }), [
    'Uncommitted changes',
    'Commits not pushed',
    'Locked'
  ]);

  // The lock reason git recorded is worth showing on hover, when there is one.
  const [lock] = worktreeChips(worktree({ isLocked: true, lockedReason: 'agent running' }));
  assert.match(lock.title, /agent running/);
  assert.equal(lock.tone, 'danger');
}

// ── would a plain remove be refused, and why ─────────────────────────────────
{
  assert.equal(safeRemoveBlockedReason(worktree()), '');
  assert.equal(
    safeRemoveBlockedReason(worktree({ isDirty: true })),
    'It has changes that were never committed.'
  );
  assert.equal(
    safeRemoveBlockedReason(worktree({ hasUnmergedCommits: true })),
    'It has commits that were never pushed to a remote.'
  );
  assert.equal(safeRemoveBlockedReason(worktree({ isLocked: true })), 'It is locked.');
  assert.equal(
    safeRemoveBlockedReason(worktree({ isDirty: true, hasUnmergedCommits: true })),
    'It has changes that were never committed. It has commits that were never pushed to a remote.'
  );
  // A folder that is already gone is not blocked — removing it just tidies up
  // the note git kept about it.
  assert.equal(safeRemoveBlockedReason(worktree({ isPrunable: true })), '');
}

// ── the rows themselves ──────────────────────────────────────────────────────
{
  const rows = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: PRIMARY, branch: 'main', lastActivity: '2026-07-20T12:00:00Z' }),
      worktree({
        path: '/w/older',
        branch: 'tsk-1-older',
        taskID: 'TSK-1',
        lastActivity: '2026-07-25T12:00:00Z'
      }),
      worktree({ path: '/w/newer', branch: 'tsk-2-newer', lastActivity: '2026-07-29T11:00:00Z' }),
      worktree({ path: '/w/unknown', branch: 'tsk-3-unknown', lastActivity: null })
    ],
    repositories: [summary({ path: '/w/newer', ahead: 2, behind: 0 })],
    sessions: [session({ ownedId: 'a', cwd: '/w/newer', state: 'live' })],
    primaryPath: PRIMARY,
    now: NOW
  });

  // The main checkout is first and is never a cleanup candidate; the rest are
  // newest-first, with "never seen anything happen" at the end.
  assert.deepEqual(
    rows.map((row) => row.branch),
    ['main', 'tsk-2-newer', 'tsk-1-older', 'tsk-3-unknown']
  );
  assert.equal(rows[0].isPrimary, true);
  assert.equal(rows[1].isPrimary, false);
  assert.equal(rows[0].safety.kind, 'protected');
  assert.equal(rows[0].lane.label, 'Keep');
  assert.equal(rows[0].canRemove, false);

  const newer = rows[1];
  assert.equal(newer.folderName, 'newer');
  assert.equal(newer.age, '1h ago');
  assert.equal(newer.aheadBehindLabel, '2 commits to push');
  assert.deepEqual(
    newer.sessions.map((link) => link.ownedId),
    ['a']
  );
  assert.equal(newer.sessionsLabel, '1 session worked here, 1 still running');
  assert.equal(newer.canRemove, true);
  // The copyable commands come from the tested safety module, not from here.
  assert.match(newer.commands.audit, /^git -C /);
  assert.match(newer.commands.cleanup, /worktree remove/);
  assert.ok(newer.commands.backup.length > 0);

  // No summary was read for this one, so it says nothing about a remote rather
  // than claiming it is up to date.
  assert.equal(rows[2].aheadBehindLabel, '');
  assert.equal(rows[3].age, 'no activity recorded');
  assert.equal(rows[3].taskId, null);
  assert.equal(rows[2].taskId, 'TSK-1');
}

// The headline over the list counts what a person is about to decide about.
{
  const rows = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: PRIMARY, branch: 'main' }),
      worktree({ path: '/w/a', branch: 'a' }),
      worktree({ path: '/w/b', branch: 'b', isDirty: true }),
      worktree({ path: '/w/c', branch: 'c', isLocked: true })
    ],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  });
  assert.equal(
    summarizeWorktreeManager(rows),
    '4 worktrees: 1 main checkout, 1 safe to remove, 2 need a decision'
  );
  assert.equal(summarizeWorktreeManager([]), 'No worktrees');
}

// ── what a forced remove destroys, in the words the dialog shows ─────────────
{
  const [row] = buildWorktreeManagerRows({
    worktrees: [
      worktree({
        path: '/w/messy',
        branch: 'tsk-9-messy',
        isDirty: true,
        hasUnmergedCommits: true,
        isLocked: true,
        lockedReason: 'agent running'
      })
    ],
    repositories: [summary({ path: '/w/messy', ahead: 4, behind: 0 })],
    sessions: [session({ ownedId: 'a', cwd: '/w/messy', state: 'live' })],
    primaryPath: PRIMARY,
    now: NOW
  });

  const losses = describeForcedRemoval(row);
  assert.ok(
    losses.some((line) => /never committed/.test(line)),
    'says the uncommitted changes go'
  );
  assert.ok(
    losses.some((line) => /4 commits/.test(line) && /remote/.test(line)),
    'says how many commits have never reached a remote'
  );
  assert.ok(
    losses.some((line) => /locked/i.test(line) && /agent running/.test(line)),
    'says the lock will be undone, and what it was for'
  );
  assert.ok(
    losses.some((line) => /1 session/.test(line)),
    'says a session is still pointed at this folder'
  );
  assert.ok(
    losses.every((line) => /[.]$/.test(line)),
    'every line is a sentence'
  );

  // The word you have to type to go through with it is the folder name, not the
  // branch — that is what is on disk and about to be deleted.
  assert.equal(row.folderName, 'messy');
}

// A forced remove of a clean worktree still says what happens, honestly.
{
  const [row] = buildWorktreeManagerRows({
    worktrees: [worktree({ path: '/w/clean', branch: 'clean' })],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  });
  const losses = describeForcedRemoval(row);
  assert.equal(losses.length, 1);
  assert.match(losses[0], /deletes the folder/i);
}

// ── the question asked before anything is removed ────────────────────────────
// A real person clicked the button on ONE row whose folder was gone and watched
// TWO rows disappear, with nothing asked and nothing said. These are the
// sentences that stop that happening again, so they are checked word for word.
{
  const gone = worktree({
    path: '/w/gone-one',
    branch: 'tsk-31-gone-one',
    isPrunable: true,
    prunableReason: 'gitdir file points at nothing'
  });
  const alsoGone = worktree({ path: '/w/gone-two', branch: 'tsk-32-gone-two', isPrunable: true });
  const rows = buildWorktreeManagerRows({
    worktrees: [worktree({ path: PRIMARY, branch: 'main' }), gone, alsoGone],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  });
  const row = rows.find((entry) => entry.branch === 'tsk-31-gone-one');

  // A row whose folder is gone is marked as such, and is the only kind of row
  // that gets the single "Clear this entry" action.
  assert.equal(row.folderGone, true);
  assert.equal(row.chips.find((chip) => chip.id === 'prunable').short, 'Folder gone');
  assert.deepEqual(otherFolderGoneBranches(rows, row), ['tsk-32-gone-two']);

  // The app build that can clear one row promises exactly that.
  const scoped = describeRemovalQuestion(row, {
    kind: 'clear',
    pruneSingleRow: 'available',
    otherFolderGoneBranches: ['tsk-32-gone-two']
  });
  assert.equal(scoped.title, 'Clear git’s record of “tsk-31-gone-one”?');
  assert.equal(
    scoped.intro,
    'The folder is already gone. This only clears git’s records for it — nothing on disk is touched.'
  );
  assert.equal(scoped.confirmLabel, 'Clear this entry');
  assert.equal(scoped.requiresTypedName, false);
  assert.equal(scoped.destructive, false);
  assert.ok(scoped.lines.includes('Only this row is cleared. Every other row is left exactly as it is.'));
  assert.ok(!scoped.lines.some((line) => line.includes('older')));

  // The older app build says so, and names what else goes.
  const wide = describeRemovalQuestion(row, {
    kind: 'clear',
    pruneSingleRow: 'unavailable',
    otherFolderGoneBranches: ['tsk-32-gone-two']
  });
  assert.ok(
    wide.lines.includes(
      'Because this app build is older, clearing this row will also clear every other row whose folder is gone.'
    )
  );
  assert.ok(
    wide.lines.includes('Right now that would also clear 1 other row: “tsk-32-gone-two”.')
  );

  // Before the app has answered, nothing is claimed either way.
  const unsure = describeRemovalQuestion(row, {
    kind: 'clear',
    pruneSingleRow: 'unknown',
    otherFolderGoneBranches: []
  });
  assert.ok(unsure.lines.some((line) => line.startsWith('The app has not answered yet')));
  assert.ok(!unsure.lines.some((line) => line.includes('Because this app build is older')));
  assert.ok(!unsure.lines.some((line) => line.includes('Only this row is cleared')));
}

// The everyday remove is asked about too, and says what it will delete.
{
  const rows = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: PRIMARY, branch: 'main' }),
      worktree({ path: '/w/clean', branch: 'tsk-40-clean' })
    ],
    repositories: [summary({ path: '/w/clean', ahead: 2, behind: 0 })],
    sessions: [session({ cwd: '/w/clean', state: 'live' })],
    primaryPath: PRIMARY,
    now: NOW
  });
  const clean = rows.find((entry) => entry.branch === 'tsk-40-clean');
  const question = describeRemovalQuestion(clean, {
    kind: 'remove',
    pruneSingleRow: 'available',
    otherFolderGoneBranches: []
  });
  assert.equal(question.title, 'Remove the worktree “tsk-40-clean”?');
  assert.equal(question.confirmLabel, 'Remove it');
  assert.equal(question.requiresTypedName, false);
  assert.ok(question.lines.includes('Deletes the folder /w/clean from your disk.'));
  assert.ok(
    question.lines.includes('This branch still has 2 commits that its remote does not have.')
  );
  assert.ok(question.lines.some((line) => line.includes('running right now')));
}

// The destructive one still asks for the folder name typed out.
{
  const rows = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: PRIMARY, branch: 'main' }),
      worktree({ path: '/w/messy', branch: 'tsk-41-messy', isDirty: true })
    ],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  });
  const messy = rows.find((entry) => entry.branch === 'tsk-41-messy');
  const question = describeRemovalQuestion(messy, {
    kind: 'force',
    pruneSingleRow: 'available',
    otherFolderGoneBranches: []
  });
  assert.equal(question.requiresTypedName, true);
  assert.equal(question.destructive, true);
  assert.equal(question.confirmLabel, 'Delete it');
  assert.equal(question.title, 'Delete the folder “messy” and everything left in it?');
  assert.deepEqual(question.lines, describeForcedRemoval(messy));
}

// ── the facts under an open row ──────────────────────────────────────────────
// The closed row used to carry these separated by "·", so a row with nothing to
// say showed separators around empty space. A fact with nothing in it is left
// out here instead.
{
  const withNothing = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: PRIMARY, branch: 'main' }),
      worktree({ path: '/w/quiet', branch: 'tsk-50-quiet' })
    ],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  }).find((entry) => entry.branch === 'tsk-50-quiet');
  const facts = worktreeFacts(withNothing);
  assert.deepEqual(
    facts.map((fact) => fact.label),
    ['Folder', 'Last activity', 'Sessions']
  );
  assert.ok(facts.every((fact) => fact.value.trim() !== ''));

  const withRemote = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: PRIMARY, branch: 'main' }),
      worktree({ path: '/w/loud', branch: 'tsk-51-loud', lastActivity: '2026-07-29T09:00:00Z' })
    ],
    repositories: [summary({ path: '/w/loud', ahead: 1, behind: 0 })],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  }).find((entry) => entry.branch === 'tsk-51-loud');
  const loudFacts = worktreeFacts(withRemote);
  assert.deepEqual(
    loudFacts.map((fact) => fact.label),
    ['Folder', 'Last activity', 'Remote', 'Sessions']
  );
  assert.equal(loudFacts.find((fact) => fact.label === 'Last activity').stamp, '2026-07-29T09:00:00Z');

  const goneRow = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: PRIMARY, branch: 'main' }),
      worktree({ path: '/w/vanished', branch: 'tsk-52-vanished', isPrunable: true })
    ],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  }).find((entry) => entry.branch === 'tsk-52-vanished');
  assert.equal(
    worktreeFacts(goneRow).find((fact) => fact.label === 'Folder').value,
    '/w/vanished — not on disk any more'
  );
}

// ── the filter box ───────────────────────────────────────────────────────────
{
  const rows = buildWorktreeManagerRows({
    worktrees: [
      worktree({ path: '/w/alpha', branch: 'tsk-11-alpha', taskID: 'TSK-11' }),
      worktree({ path: '/w/beta', branch: 'feature/beta' })
    ],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  });
  assert.equal(filterWorktreeRows(rows, '').length, 2);
  assert.equal(filterWorktreeRows(rows, '   ').length, 2);
  assert.deepEqual(
    filterWorktreeRows(rows, 'BETA').map((row) => row.branch),
    ['feature/beta']
  );
  assert.deepEqual(
    filterWorktreeRows(rows, 'tsk-11').map((row) => row.branch),
    ['tsk-11-alpha']
  );
  assert.deepEqual(
    filterWorktreeRows(rows, '/w/alpha').map((row) => row.branch),
    ['tsk-11-alpha']
  );
  assert.equal(filterWorktreeRows(rows, 'nothing here').length, 0);
}

console.log('worktreeManager: all tests passed');
