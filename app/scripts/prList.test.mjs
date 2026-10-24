/**
 * prList.test.mjs — the open-pull-request list without a GitHub CLI.
 *
 * The case worth its own state is `gh` not being installed: that is a missing
 * tool with a one-line fix, not a failure of the repository, so it must never
 * arrive as a red spawn error.
 */
import assert from 'node:assert/strict';

import {
  NO_GH_HINT,
  NO_GH_MESSAGE,
  beginPullRequestListLoad,
  checkTone,
  createPullRequestListModel,
  describeChecks,
  describePullRequestListSummary,
  describePullRequestRow,
  failPullRequestList,
  finishPullRequestListLoad,
  isMissingGhError
} from '../src/lib/shell/git/components/pr/prList.ts';

function pullRequest(overrides = {}) {
  return {
    number: 24,
    title: 'Add the git panel',
    url: 'https://example.test/pull/24',
    state: 'OPEN',
    headBranch: 'tsk-808-git-panel',
    isDraft: false,
    checks: 'passing',
    checkSummary: '3 passing · 0 pending · 0 failing',
    ...overrides
  };
}

let model = createPullRequestListModel();
assert.equal(model.state, 'idle');
assert.equal(describePullRequestListSummary(model), '');

model = beginPullRequestListLoad(model);
assert.equal(describePullRequestListSummary(model), 'Reading open pull requests…');

model = finishPullRequestListLoad(model, [pullRequest()]);
assert.equal(model.state, 'ready');
assert.equal(describePullRequestListSummary(model), '1 open pull request');

model = finishPullRequestListLoad(model, [pullRequest(), pullRequest({ number: 25 })]);
assert.equal(describePullRequestListSummary(model), '2 open pull requests');

model = finishPullRequestListLoad(model, []);
assert.equal(describePullRequestListSummary(model), 'No open pull requests.');

// ── checks ──────────────────────────────────────────────────────────────────
assert.equal(describeChecks(pullRequest()), '3 passing · 0 pending · 0 failing');
assert.equal(describeChecks(pullRequest({ checks: 'none', checkSummary: '' })), 'no checks');
assert.equal(describeChecks(pullRequest({ checks: 'pending', checkSummary: '' })), 'checks pending');
assert.equal(checkTone(pullRequest()), 'good');
assert.equal(checkTone(pullRequest({ checks: 'failing' })), 'bad');
assert.equal(checkTone(pullRequest({ checks: 'pending' })), 'attention');
assert.equal(checkTone(pullRequest({ checks: 'none' })), 'quiet');
assert.equal(
  describePullRequestRow(pullRequest({ isDraft: true })),
  'Draft · tsk-808-git-panel · 3 passing · 0 pending · 0 failing'
);

// ── gh missing gets its own state and its own words ─────────────────────────
assert.equal(isMissingGhError(new Error(NO_GH_MESSAGE)), true);
assert.equal(isMissingGhError('gh is not on PATH'), true);
assert.equal(isMissingGhError(new Error('gh pr list failed: not signed in')), false);

const noGh = failPullRequestList(createPullRequestListModel(), new Error(NO_GH_MESSAGE));
assert.equal(noGh.state, 'no-gh');
assert.equal(noGh.error, NO_GH_MESSAGE);
assert.equal(describePullRequestListSummary(noGh), NO_GH_MESSAGE);
assert.ok(NO_GH_HINT.includes('brew install gh'), 'the fix is spelled out, not implied');

const failed = failPullRequestList(
  finishPullRequestListLoad(createPullRequestListModel(), [pullRequest()]),
  new Error('gh pr list failed: not signed in')
);
assert.equal(failed.state, 'failed');
assert.equal(failed.error, 'gh pr list failed: not signed in', "gh's own words survive");
assert.equal(
  failed.pullRequests.length,
  1,
  'a failed refresh keeps what is already on screen rather than blanking it'
);

const blank = failPullRequestList(createPullRequestListModel(), new Error('   '));
assert.equal(blank.error, 'Could not read the open pull requests.');

console.log('prList tests passed');
