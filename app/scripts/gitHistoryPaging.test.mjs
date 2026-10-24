/**
 * gitHistoryPaging.test.mjs — reading more of the commit history, and saying
 * honestly how much of it is on screen.
 *
 * The backend returns one bounded cursor page. Load More appends only that next
 * page; refresh and root changes return to the first page.
 *
 * `gitPanelStore.svelte.ts` calls `$state(...)` at module scope, which the
 * Svelte compiler normally rewrites; Node runs it as plain JavaScript, so the
 * one-line stand-in below gives `$state(v)` its plain-value meaning first.
 */
globalThis.$state = (value) => value;

const assert = (await import('node:assert/strict')).default;

const store = await import('../src/lib/shell/git/gitPanelStore.svelte.ts');
const service = await import('../src/lib/shell/git/gitService.ts');

const {
  canLoadMoreGitHistory,
  createGitPanelState,
  describeGitBranchTitle,
  describeGitHistoryCount,
  describeGitHistoryFooter
} = store;

const { COMMIT_HISTORY_LIMIT, createGitService } = service;

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/** One commit record, shaped like the backend's `GitCommitHistoryEntry`. */
function commit(index) {
  const sha = String(index).padStart(40, '0');
  return {
    shortSha: sha.slice(0, 7),
    sha,
    subject: `commit ${index}`,
    author: 'Someone',
    committedAt: '2026-07-30T09:00:00+01:00',
    refNames: '',
    parentShas: index > 1 ? [String(index - 1).padStart(40, '0')] : []
  };
}

const emptyStatus = {
  branch: 'main',
  ahead: 0,
  behind: 0,
  hasUpstream: true,
  files: []
};

/**
 * A backend holding `total` commits and returning one fixed cursor page.
 */
function makeBackend({ total = 1000 } = {}) {
  const calls = [];
  const all = Array.from({ length: total }, (_, index) => commit(total - index));
  const refuse = async () => {
    throw new Error('this test never writes');
  };
  return {
    calls,
    async readStatus(root) {
      calls.push(['readStatus', root]);
      return emptyStatus;
    },
    async readDiff() {
      return null;
    },
    async readHistory(root, cursor, relativePath) {
      calls.push(['readHistory', root, cursor, relativePath]);
      const offset = Number.parseInt(cursor ?? '0', 10);
      const commits = all.slice(offset, offset + COMMIT_HISTORY_LIMIT);
      const complete = offset + commits.length >= all.length;
      return {
        root,
        relativePath: relativePath ?? null,
        commits,
        nextCursor: complete ? null : String(offset + commits.length),
        complete
      };
    },
    stage: refuse,
    unstage: refuse,
    commit: refuse,
    fetch: refuse,
    pull: refuse,
    push: refuse
  };
}

// ── a repository with plenty of history: load more grows the list ───────────
{
  const backend = makeBackend({ total: 1000 });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();
  assert.equal(state.history.length, COMMIT_HISTORY_LIMIT);
  assert.equal(state.historyRequested, COMMIT_HISTORY_LIMIT);
  assert.equal(state.historyComplete, false);
  assert.equal(describeGitHistoryCount(state), '24 so far');
  assert.equal(describeGitHistoryFooter(state), 'Showing 24 so far.');
  assert.equal(canLoadMoreGitHistory(state), true);

  backend.calls.length = 0;
  await git.loadMoreHistory();
  await settle();
  assert.deepEqual(
    backend.calls,
    [['readHistory', '/repo', '24', null]],
    'one read using the continuation cursor'
  );
  assert.equal(state.history.length, 48, 'the next page is appended');
  assert.equal(state.historyPaged, true);
  assert.equal(state.historyComplete, false);
  assert.equal(describeGitHistoryFooter(state), 'Showing 48 so far.');

  // The newest commit is still first: a page read from the top keeps the order
  // the graph's columns were worked out from.
  assert.equal(state.history[0].subject, 'commit 1000');
}

// A cursor may repeat its boundary commit. The list still contains each SHA
// once, because duplicate keyed rows crash every Svelte history surface.
{
  const backend = makeBackend({ total: 48 });
  const originalReadHistory = backend.readHistory;
  let previousBoundary = null;
  backend.readHistory = async (...args) => {
    const page = await originalReadHistory(...args);
    if (args[1] !== null && previousBoundary) page.commits.unshift(previousBoundary);
    previousBoundary = page.commits.at(-1) ?? previousBoundary;
    return page;
  };
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();
  await git.loadMoreHistory();

  assert.equal(state.history.length, 48);
  assert.equal(new Set(state.history.map((entry) => entry.sha)).size, 48);
}

// ── two visible history surfaces page independently ────────────────────────
{
  const leftState = createGitPanelState();
  const rightState = createGitPanelState();
  const left = createGitService({ backend: makeBackend({ total: 1000 }), state: leftState });
  const right = createGitService({ backend: makeBackend({ total: 1000 }), state: rightState });

  left.activate('/repo');
  left.ensureHistorySurface();
  right.activate('/repo');
  right.ensureHistorySurface();
  await settle();

  await left.loadMoreHistory();
  assert.equal(leftState.history.length, 48);
  assert.equal(rightState.history.length, 24);
}

// ── a small repository: the first read already has everything ───────────────
{
  const backend = makeBackend({ total: 7 });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();

  assert.equal(state.history.length, 7);
  assert.equal(state.historyComplete, true);
  assert.equal(canLoadMoreGitHistory(state), false, 'nothing to load, so nothing is offered');
  assert.equal(describeGitHistoryCount(state), '7');
  assert.equal(
    describeGitHistoryFooter(state),
    'Showing all 7 commits.',
    'asking for 24 and getting 7 really is the whole history'
  );
}

// ── refreshing releases older pages and reads the first page again ──────────
{
  const backend = makeBackend({ total: 1000 });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();
  await git.loadMoreHistory();
  await settle();

  backend.calls.length = 0;
  await git.refreshHistory();
  await settle();
  assert.deepEqual(
    backend.calls,
    [['readHistory', '/repo', null, null]],
    'refresh starts a new first-page projection'
  );
  assert.equal(state.history.length, COMMIT_HISTORY_LIMIT);
}

// ── pointing the panel at another repository starts over ────────────────────
{
  const backend = makeBackend({ total: 1000 });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();
  await git.loadMoreHistory();
  await settle();

  git.activate('/other');
  git.ensureHistorySurface();
  await settle();
  assert.equal(state.historyRequested, COMMIT_HISTORY_LIMIT);
  assert.equal(state.historyPaged, false);
  assert.equal(state.history.length, COMMIT_HISTORY_LIMIT);
}

// ── cursor completion stops further reads ───────────────────────────────────
{
  const backend = makeBackend({ total: 50 });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();
  for (let click = 0; click < 4; click += 1) {
    await git.loadMoreHistory();
    await settle();
  }

  assert.equal(state.historyRequested, 50);
  assert.equal(state.history.length, 50);
  assert.equal(state.historyComplete, true);
  assert.equal(canLoadMoreGitHistory(state), false);
  assert.equal(describeGitHistoryFooter(state), 'Showing all 50 commits loaded across pages.');
  assert.equal(
    backend.calls.filter((call) => call[0] === 'readHistory').length,
    3,
    'completion prevents repeated reads'
  );
}

// ── a failed "load more" keeps what is already on screen ────────────────────
{
  const backend = makeBackend({ total: 1000 });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();

  backend.readHistory = async () => {
    throw new Error('fatal: bad object HEAD');
  };
  await git.loadMoreHistory();
  await settle();

  assert.equal(state.history.length, COMMIT_HISTORY_LIMIT, 'the 24 already read stay on screen');
  assert.equal(state.historyError, 'fatal: bad object HEAD');
  assert.equal(state.historyLoadingMore, false);
}

// ── a failed refresh has nothing left to show, and says so ──────────────────
{
  const backend = makeBackend({ total: 1000 });
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  git.activate('/repo');
  git.ensureHistorySurface();
  await settle();

  backend.readHistory = async () => {
    throw new Error('fatal: bad object HEAD');
  };
  await git.refreshHistory();
  await settle();

  assert.equal(state.history.length, 0);
  assert.equal(state.historyError, 'fatal: bad object HEAD');
  assert.equal(describeGitHistoryFooter(state), '', 'no list, no count line');
}

// ── a failed first read waits for Refresh instead of reading on every pass ──
// Regression a89037a49: the effects that call ensureHistorySurface ran it again
// after each failure, so a plain folder read its history ~33 times a second.
{
  const backend = makeBackend({ total: 1000 });
  const working = backend.readHistory;
  backend.readHistory = async (...args) => {
    backend.calls.push(['readHistory', ...args]);
    throw new Error('fatal: not a git repository');
  };
  const reads = () => backend.calls.filter(([name]) => name === 'readHistory').length;
  const state = createGitPanelState();
  const git = createGitService({ backend, state });

  // Show the surface before a root is set so the first read is the awaitable
  // refreshHistory; the test then waits on that read instead of a timer.
  git.ensureHistorySurface();
  git.activate('/plain-folder');
  await git.refreshHistory();
  assert.equal(state.historyError, 'fatal: not a git repository');
  const afterFailure = reads();

  // ensureHistorySurface starts any read synchronously, so the count is exact here.
  git.ensureHistorySurface();
  git.ensureHistorySurface();
  git.ensureHistorySurface();
  assert.equal(reads(), afterFailure, 'later effect passes do not read a failed history again');

  backend.readHistory = working;
  await git.refreshHistory();
  assert.equal(state.historyError, '');
  assert.equal(state.history.length, COMMIT_HISTORY_LIMIT, 'Refresh still reads again');
}

// ── the branch hover text spells everything out ─────────────────────────────
{
  assert.equal(describeGitBranchTitle(null), 'No repository loaded');

  assert.equal(
    describeGitBranchTitle({
      branch: 'codex/outbound-rule-generation',
      ahead: 0,
      behind: 0,
      hasUpstream: true,
      files: []
    }),
    'Branch: codex/outbound-rule-generation\nUp to date with the remote.'
  );

  assert.equal(
    describeGitBranchTitle({
      branch: 'tsk-798-polish-wave',
      ahead: 1,
      behind: 3,
      hasUpstream: true,
      files: []
    }),
    'Branch: tsk-798-polish-wave\n' +
      '1 commit here that the remote does not have.\n' +
      '3 commits on the remote that you do not have.'
  );

  assert.equal(
    describeGitBranchTitle({
      branch: 'new-idea',
      ahead: 0,
      behind: 0,
      hasUpstream: false,
      files: []
    }),
    'Branch: new-idea\nThis branch has no matching branch on the remote yet.'
  );
}

console.log('gitHistoryPaging: all tests passed');
