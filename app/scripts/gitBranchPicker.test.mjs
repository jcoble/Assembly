/**
 * gitBranchPicker.test.mjs — the branch menu's view model.
 *
 * The names refused here are the ones git itself refuses plus the ones that
 * could be read as an option on a command line. The Rust side checks the same
 * list before it runs anything; this copy exists so the reason appears under
 * the box while typing.
 */
import assert from 'node:assert/strict';

import {
  branchNameProblem,
  branchWorktreeRoot,
  canCreateBranch,
  describeBranchRow,
  describeCurrentBranch,
  filterBranches
} from '../src/lib/shell/git/components/branchPicker.ts';

const branches = [
  { name: 'main', isCurrent: true, upstream: 'origin/main', worktreePath: '/repo', subject: 'latest work' },
  { name: 'tsk-808-git-panel', isCurrent: false, upstream: '', worktreePath: '/worktrees/tsk-808', subject: 'wire the panel' },
  { name: 'spike/colors', isCurrent: false, upstream: 'origin/spike/colors', worktreePath: '', subject: '' }
];

assert.equal(filterBranches(branches, '').length, 3);
assert.equal(filterBranches(branches, '  ').length, 3, 'a whitespace filter is an empty filter');
assert.deepEqual(
  filterBranches(branches, 'TSK').map((branch) => branch.name),
  ['tsk-808-git-panel'],
  'the filter ignores case'
);
assert.equal(filterBranches(branches, 'nothing').length, 0);

assert.equal(describeBranchRow(branches[0]), 'tracks origin/main · latest work');
assert.equal(describeBranchRow(branches[1]), 'no remote branch · wire the panel');
assert.equal(describeBranchRow(branches[2]), 'tracks origin/spike/colors');

// A branch checked out in another worktree is reached by moving the session there.
assert.equal(branchWorktreeRoot(branches[1]), '/worktrees/tsk-808');
// On a remote machine the bridge has already prefixed the path; it is passed on unchanged.
assert.equal(
  branchWorktreeRoot({ ...branches[1], worktreePath: 'assembly-remote://workbox/worktrees/tsk-808' }),
  'assembly-remote://workbox/worktrees/tsk-808'
);
assert.equal(branchWorktreeRoot(branches[0]), null, 'the current branch is already here');
assert.equal(branchWorktreeRoot(branches[2]), null, 'a branch with no worktree is checked out here');

assert.equal(describeCurrentBranch(null), 'no branch checked out');
assert.equal(describeCurrentBranch('  '), 'no branch checked out');
assert.equal(describeCurrentBranch('main'), 'main');

const existing = branches.map((branch) => branch.name);
assert.equal(branchNameProblem('', existing), '', 'an empty box is not a mistake');
assert.equal(branchNameProblem('tsk-809-next', existing), '');
assert.equal(branchNameProblem('main', existing), 'main already exists.');
assert.equal(branchNameProblem('my branch', existing), 'Branch names cannot contain spaces.');
assert.equal(branchNameProblem('--force', existing), 'A branch name cannot start with a dash.');
assert.equal(branchNameProblem('a~b', existing), 'Branch names cannot contain ~');
assert.equal(branchNameProblem('a:b', existing), 'Branch names cannot contain :');
assert.equal(
  branchNameProblem('a..b', existing),
  'Branch names cannot contain two dots in a row.'
);
assert.equal(
  branchNameProblem('feature/', existing),
  'Branch names cannot start or end with a slash.'
);
assert.equal(branchNameProblem('feature.lock', existing), 'Branch names cannot end with .lock');
assert.equal(branchNameProblem('a'.repeat(201), existing), 'That branch name is too long.');

assert.equal(canCreateBranch('tsk-809', existing, { canWrite: true, busy: false }), true);
assert.equal(canCreateBranch('', existing, { canWrite: true, busy: false }), false);
assert.equal(canCreateBranch('main', existing, { canWrite: true, busy: false }), false);
assert.equal(
  canCreateBranch('tsk-809', existing, { canWrite: false, busy: false }),
  false,
  'a page that cannot change the repository cannot create a branch'
);
assert.equal(canCreateBranch('tsk-809', existing, { canWrite: true, busy: true }), false);

console.log('gitBranchPicker tests passed');
