import assert from 'node:assert/strict';

import {
  buildGitCommitGraphRows,
  buildGitGraphViewModel,
  buildGitRepositoryGraphRows,
  gitCommitParentHint,
  gitCommitRefSummary,
  gitRepositoryDirtySummary,
  gitRepositorySyncSummary
} from '../src/lib/shell/git/gitGraphViewModel.ts';

const dirtyRepo = {
  projectID: 'mac-command-bar',
  projectName: 'MacCommandBar',
  repo: 'mac-command-bar',
  path: '/repo/mac-command-bar',
  rootLabel: 'main checkout',
  branch: 'cdx/tsk-127-128-git-panel',
  taskID: null,
  isWorktree: false,
  isDirty: true,
  stagedCount: 1,
  unstagedCount: 2,
  untrackedCount: 1,
  dirtyCount: 4,
  ahead: 2,
  behind: 1,
  hasUpstream: true,
  lastCommitSha: 'abc1234',
  lastCommitSubject: 'feat: add TSK-129 graph rows',
  lastCommitAt: '2026-06-15T12:00:00Z',
  dirtySinceEpochMs: 1_000,
  dirtyStatusFingerprint: 'dirty',
  error: null
};

assert.deepEqual(gitRepositoryDirtySummary(dirtyRepo, { now: 121_000 }), {
  isDirty: true,
  dirtyCount: 4,
  stagedCount: 1,
  unstagedCount: 2,
  untrackedCount: 1,
  label: '4 changed · staged 1 / unstaged 2 / untracked 1 · 2m',
  countLabel: '4 changed',
  detailLabel: 'staged 1 / unstaged 2 / untracked 1',
  tone: 'dirty',
  dirtySinceEpochMs: 1_000,
  dirtyAgeMs: 120_000,
  dirtyAgeLabel: '2m'
});

assert.deepEqual(gitRepositorySyncSummary(dirtyRepo), {
  ahead: 2,
  behind: 1,
  hasUpstream: true,
  label: 'ahead 2 / behind 1',
  detailLabel: 'local and upstream both have unique commits',
  tone: 'diverged'
});

assert.deepEqual(
  gitRepositorySyncSummary({
    ...dirtyRepo,
    isDirty: false,
    dirtyCount: 0,
    stagedCount: 0,
    unstagedCount: 0,
    untrackedCount: 0,
    branch: 'main',
    taskID: null,
    ahead: 0,
    behind: 0,
    hasUpstream: false
  }),
  {
    ahead: 0,
    behind: 0,
    hasUpstream: false,
    label: 'no upstream',
    detailLabel: 'no upstream configured',
    tone: 'no-upstream'
  }
);

const repoRows = buildGitRepositoryGraphRows([dirtyRepo], { now: 121_000 });
assert.equal(repoRows.length, 1);
assert.equal(repoRows[0].id, 'repository:/repo/mac-command-bar');
assert.equal(repoRows[0].branchLabel, 'cdx/tsk-127-128-git-panel');
assert.equal(repoRows[0].headLabel, 'abc1234');
assert.equal(repoRows[0].taskID, 'TSK-127');
assert.deepEqual(
  repoRows[0].taskReferences.map((task) => task.id),
  ['TSK-127', 'TSK-128', 'TSK-129']
);
assert.deepEqual(repoRows[0].taskSearchTargets[1], {
  kind: 'notion-search',
  id: 'TSK-128',
  label: 'TSK-128',
  query: 'TSK-128'
});
assert.equal(
  repoRows[0].compactLabel,
  'mac-command-bar · cdx/tsk-127-128-git-panel · 4 changed · staged 1 / unstaged 2 / untracked 1 · 2m · ahead 2 / behind 1'
);
assert.ok(repoRows[0].searchText.includes('TSK-129'));

const mergeCommit = {
  shortSha: 'def4567',
  sha: 'def4567890abcdef',
  subject: 'merge task 130 history helper',
  author: 'Ada Lovelace',
  committedAt: '2026-06-15T12:30:00Z',
  refs: 'HEAD -> cdx/tsk-127-128-git-panel, origin/cdx/tsk-127-git-panel, tag: v0.1.0',
  parentShas: ['1111111111111111', '2222222222222222'],
  parentCount: 2,
  taskID: 'TSK-127',
  taskSource: 'refs'
};

assert.deepEqual(gitCommitParentHint(mergeCommit), {
  kind: 'merge',
  label: '2 parents',
  parentCount: 2,
  parentShas: ['1111111111111111', '2222222222222222'],
  firstParentSha: '1111111111111111',
  isMerge: true
});

assert.deepEqual(gitCommitRefSummary(mergeCommit.refs), {
  labels: [
    'HEAD -> cdx/tsk-127-128-git-panel',
    'origin/cdx/tsk-127-git-panel',
    'tag: v0.1.0'
  ],
  headLabels: ['HEAD -> cdx/tsk-127-128-git-panel'],
  branchLabels: ['cdx/tsk-127-128-git-panel'],
  remoteLabels: ['origin/cdx/tsk-127-git-panel'],
  tagLabels: ['tag: v0.1.0'],
  label: 'HEAD -> cdx/tsk-127-128-git-panel, origin/cdx/tsk-127-git-panel, tag: v0.1.0'
});

const rootCommit = {
  shortSha: '0000001',
  sha: '000000100000010000001',
  subject: 'initial import',
  author: 'Grace Hopper',
  committedAt: '2026-06-14T09:00:00Z',
  refs: '',
  parentShas: [],
  parentCount: 0,
  taskID: null,
  taskSource: null
};

const commitRows = buildGitCommitGraphRows([mergeCommit, rootCommit]);
assert.equal(commitRows[0].graphKind, 'head');
assert.equal(commitRows[0].topologyLabel, 'HEAD');
assert.equal(commitRows[0].parentHint.kind, 'merge');
assert.equal(commitRows[0].taskID, 'TSK-127');
assert.deepEqual(
  commitRows[0].taskReferences.map((task) => task.id),
  ['TSK-127', 'TSK-128', 'TSK-130']
);
assert.deepEqual(
  commitRows[0].ownershipBadges.map((badge) => badge.label),
  ['HEAD', 'UPSTREAM', 'TAG', 'TSK-127', 'MERGE']
);
assert.ok(commitRows[0].metaLabel.includes('2 parents'));
assert.ok(commitRows[0].detailLabel.includes('2 parents'));
assert.equal(commitRows[1].graphKind, 'root');
assert.equal(commitRows[1].topologyLabel, 'ROOT');
assert.equal(commitRows[1].parentHint.label, 'root commit');
assert.equal(commitRows[1].refs.label, 'no refs');

const viewModel = buildGitGraphViewModel(
  {
    repositories: [dirtyRepo],
    commits: [mergeCommit, rootCommit]
  },
  { now: 121_000 }
);
assert.deepEqual(viewModel.taskIDs, ['TSK-127', 'TSK-128', 'TSK-129', 'TSK-130']);
assert.deepEqual(
  viewModel.taskSearchTargets.map((target) => target.query),
  ['TSK-127', 'TSK-128', 'TSK-129', 'TSK-130']
);
assert.deepEqual(viewModel.summary, {
  repositoryCount: 1,
  dirtyRepositoryCount: 1,
  commitCount: 2,
  mergeCommitCount: 1,
  taskCount: 4
});

// ── the center-lane Git History view: repo picker, filters, uncommitted row ──
// These cover `src/lib/shell/git/gitHistoryFilters.ts`, which is everything the
// center view decides before it draws anything. The view itself only turns the
// answers below into rows, so a wrong answer here is a wrong screen there.

// The filters read the changed-file grouping out of `gitPanelStore.svelte.ts`,
// which calls `$state(...)` at module scope. The Svelte compiler normally
// rewrites that; Node runs it as plain JavaScript, so give `$state(v)` its
// plain-value meaning first — the same one-line stand-in
// `gitHistoryPaging.test.mjs` uses.
globalThis.$state = (value) => value;

const {
  EMPTY_GIT_HISTORY_FILTER,
  filterGitHistoryRows,
  gitHistoryAuthors,
  gitHistoryBranches,
  gitHistoryBranchScope,
  gitHistoryRepositoryOptions,
  gitHistoryUncommittedRow,
  isGitHistoryFilterActive
} = await import('../src/lib/shell/git/gitHistoryFilters.ts');

/** A tiny history with two lines of work that come back together at `merge`.
 *
 *   merge   (HEAD -> main, origin/main)   parents: mainTip, sideTip
 *   sideTip (feature/graph)               parents: base
 *   mainTip (tag: v1.0)                   parents: base
 *   base                                  parents: (none)
 */
const historyEntries = [
  {
    shortSha: 'aaaaaaa',
    sha: 'aaaaaaa1111111111111111111111111111111',
    subject: 'Merge the graph work into main',
    author: 'Ada',
    committedAt: '2026-08-19T10:00:00Z',
    refs: 'HEAD -> main, origin/main',
    parentShas: ['bbbbbbb2222222222222222222222222222222', 'ccccccc3333333333333333333333333333333'],
    parentCount: 2,
    taskID: null,
    taskSource: null
  },
  {
    shortSha: 'ccccccc',
    sha: 'ccccccc3333333333333333333333333333333',
    subject: 'Draw the lanes',
    author: 'Grace',
    committedAt: '2026-08-18T10:00:00Z',
    refs: 'feature/graph',
    parentShas: ['ddddddd4444444444444444444444444444444'],
    parentCount: 1,
    taskID: null,
    taskSource: null
  },
  {
    shortSha: 'bbbbbbb',
    sha: 'bbbbbbb2222222222222222222222222222222',
    subject: 'Release notes',
    author: 'Ada',
    committedAt: '2026-08-17T10:00:00Z',
    refs: 'tag: v1.0',
    parentShas: ['ddddddd4444444444444444444444444444444'],
    parentCount: 1,
    taskID: null,
    taskSource: null
  },
  {
    shortSha: 'ddddddd',
    sha: 'ddddddd4444444444444444444444444444444',
    subject: 'First commit',
    author: 'Grace',
    committedAt: '2026-08-16T10:00:00Z',
    refs: '',
    parentShas: [],
    parentCount: 0,
    taskID: null,
    taskSource: null
  }
];

const historyRows = buildGitCommitGraphRows(historyEntries);
const shas = (rows) => rows.map((row) => row.shortSha);

// The author list is what the Author picker offers: every author once, in a
// settled order, so the picker does not reshuffle when a page of older commits
// arrives.
assert.deepEqual(gitHistoryAuthors(historyRows), ['Ada', 'Grace']);

// The branch list offers local and remote names, never tags — a tag is not
// something you can filter a line of history by.
assert.deepEqual(gitHistoryBranches(historyRows), ['feature/graph', 'main', 'origin/main']);

// Branch scope is reachability inside the commits that are LOADED: the feature
// branch's tip and everything it descends from, and nothing from the other line.
assert.deepEqual(
  [...gitHistoryBranchScope(historyRows, 'feature/graph')].sort(),
  [
    'ccccccc3333333333333333333333333333333',
    'ddddddd4444444444444444444444444444444'
  ],
  'a branch shows its own tip and its ancestors, not its siblings'
);
assert.equal(
  gitHistoryBranchScope(historyRows, ''),
  null,
  'no branch chosen means no scoping at all, which is not the same as an empty scope'
);
assert.deepEqual(
  [...gitHistoryBranchScope(historyRows, 'nothing-like-this')],
  [],
  'a branch none of the loaded commits carries scopes the list to nothing'
);

// "All" is the resting state and changes nothing.
assert.deepEqual(
  shas(filterGitHistoryRows(historyRows, EMPTY_GIT_HISTORY_FILTER)),
  ['aaaaaaa', 'ccccccc', 'bbbbbbb', 'ddddddd']
);
assert.equal(isGitHistoryFilterActive(EMPTY_GIT_HISTORY_FILTER), false);

assert.deepEqual(
  shas(filterGitHistoryRows(historyRows, { branch: 'main', author: '', search: '' })),
  ['aaaaaaa', 'ccccccc', 'bbbbbbb', 'ddddddd'],
  'main reaches everything here, because the merge brought the other line in'
);
assert.deepEqual(
  shas(filterGitHistoryRows(historyRows, { branch: 'feature/graph', author: '', search: '' })),
  ['ccccccc', 'ddddddd']
);
assert.deepEqual(
  shas(filterGitHistoryRows(historyRows, { branch: '', author: 'Ada', search: '' })),
  ['aaaaaaa', 'bbbbbbb']
);
assert.deepEqual(
  shas(filterGitHistoryRows(historyRows, { branch: '', author: '', search: 'lanes' })),
  ['ccccccc'],
  'the search box reads the subject'
);
assert.deepEqual(
  shas(filterGitHistoryRows(historyRows, { branch: '', author: '', search: 'BBBBBBB' })),
  ['aaaaaaa', 'bbbbbbb'],
  'searching a hash is case-blind, and finds the commit AND the merge that names it as a parent'
);
assert.deepEqual(
  shas(filterGitHistoryRows(historyRows, { branch: 'feature/graph', author: 'Grace', search: 'first' })),
  ['ddddddd'],
  'the three filters narrow together'
);
assert.equal(isGitHistoryFilterActive({ branch: '', author: '', search: '  ' }), false);
assert.equal(isGitHistoryFilterActive({ branch: 'main', author: '', search: '' }), true);

// ── the "Uncommitted Changes" row ────────────────────────────────────────────
const dirtyStatus = {
  branch: 'main',
  ahead: 0,
  behind: 0,
  hasUpstream: true,
  files: [
    { relativePath: 'a.ts', status: 'modified', badge: 'M', indexStatus: 'modified', worktreeStatus: '' },
    { relativePath: 'b.ts', status: 'modified', badge: 'M', indexStatus: '', worktreeStatus: 'modified' },
    { relativePath: 'c.ts', status: 'untracked', badge: '?', indexStatus: '?', worktreeStatus: '?' }
  ]
};

const uncommitted = gitHistoryUncommittedRow(dirtyStatus, EMPTY_GIT_HISTORY_FILTER);
assert.equal(uncommitted.changedCount, 3);
assert.equal(uncommitted.countLabel, '3 files');
assert.equal(uncommitted.detail, 'Staged 1 · Changed 1 · New files 1');
assert.equal(
  gitHistoryUncommittedRow(null, EMPTY_GIT_HISTORY_FILTER),
  null,
  'no status read yet means no row — never an invented clean one'
);
assert.equal(
  gitHistoryUncommittedRow({ ...dirtyStatus, files: [] }, EMPTY_GIT_HISTORY_FILTER),
  null,
  'a clean working copy has no row'
);
assert.equal(
  gitHistoryUncommittedRow(dirtyStatus, { branch: '', author: 'Ada', search: '' }),
  null,
  'work that is not committed yet has no author or hash to match, so a filter hides it'
);
assert.equal(
  gitHistoryUncommittedRow(dirtyStatus, { branch: 'main', author: '', search: '' }).changedCount,
  3,
  'a branch filter keeps it: uncommitted work is on whichever branch is checked out'
);

// ── the repository picker ────────────────────────────────────────────────────
const repoOptions = gitHistoryRepositoryOptions('/repo/mac-command-bar', [
  { repo: 'mac-command-bar', path: '/repo/mac-command-bar', branch: 'main' },
  { repo: 'mac-command-bar', path: '/repo/worktrees/tsk-935', branch: 'tsk-935-w22' },
  { repo: 'mac-command-bar', path: '/repo/mac-command-bar', branch: 'main' }
]);
assert.deepEqual(repoOptions, [
  { path: '/repo/mac-command-bar', label: 'mac-command-bar', branch: 'main' },
  { path: '/repo/worktrees/tsk-935', label: 'tsk-935', branch: 'tsk-935-w22' }
]);
assert.deepEqual(
  gitHistoryRepositoryOptions('/repo/only-this-one', []),
  [{ path: '/repo/only-this-one', label: 'only-this-one', branch: '' }],
  'the folder in front is always offered, even before any worktree has been read'
);
assert.deepEqual(gitHistoryRepositoryOptions(null, []), []);

console.log('gitGraphViewModel: history filters, uncommitted row and repository picker passed');
