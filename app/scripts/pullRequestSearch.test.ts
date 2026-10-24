import assert from 'node:assert/strict';

import { pullRequestSearchQuery, sessionProjectFilter } from '../src/lib/shell/git/github/pullRequestSearch.ts';

// Every state reaches GitHub as its own qualifier; "all" adds none, so merged
// and closed pull requests are searchable too.
assert.equal(pullRequestSearchQuery('open', 'everyone', ''), 'is:open sort:updated-desc');
assert.equal(pullRequestSearchQuery('merged', 'everyone', ''), 'is:merged sort:updated-desc');
assert.equal(pullRequestSearchQuery('closed', 'everyone', ''), 'is:closed is:unmerged sort:updated-desc');
assert.equal(pullRequestSearchQuery('all', 'everyone', ''), 'sort:updated-desc');

// The scope narrows by who, independent of the state.
assert.equal(pullRequestSearchQuery('all', 'mine', ''), 'author:@me sort:updated-desc');
assert.equal(pullRequestSearchQuery('merged', 'needs-review', ''), 'is:merged review-requested:@me sort:updated-desc');

// A plain word matches the title or body, the head branch (by prefix), or the
// author, so a branch name or a login typed on its own finds its pull requests.
assert.equal(
  pullRequestSearchQuery('open', 'mine', '  adapter   jcoble \n'),
  'is:open author:@me (adapter OR head:adapter OR author:adapter) (jcoble OR head:jcoble OR author:jcoble) sort:updated-desc'
);
assert.equal(
  pullRequestSearchQuery('all', 'everyone', 'cdx/selected-provider'),
  '(cdx/selected-provider OR head:cdx/selected-provider OR author:cdx/selected-provider) sort:updated-desc'
);

// Anything else is GitHub search syntax and passes through untouched: a
// qualifier, or every word of a quoted phrase.
assert.equal(pullRequestSearchQuery('all', 'everyone', 'head:fix label:bug'), 'head:fix label:bug sort:updated-desc');
assert.equal(
  pullRequestSearchQuery('all', 'everyone', '"selected adapter" show'),
  '"selected adapter" (show OR head:show OR author:show) sort:updated-desc'
);

// A number names one pull request, so it is found whatever its state or author.
assert.equal(pullRequestSearchQuery('open', 'mine', '111'), '111 sort:updated-desc');
assert.equal(pullRequestSearchQuery('open', 'needs-review', ' #111 '), '111 sort:updated-desc');

// The project filter follows the active session: its GitHub project by id,
// then by the project group a worktree session shares with it; a remote
// session searches its own workspace; anything else lists every project.
const projects = [
  { id: 'p1', machine: 'local', rootPath: '/code/test1234', repoKey: 'local/test1234', groupKey: 'project:p1' },
  { id: 'p2', machine: 'local', rootPath: '/code/assembly', repoKey: 'github.com/jcoble/mac-command-bar', groupKey: 'repo:github.com/jcoble/mac-command-bar' },
  { id: 'p3', machine: 'workbox', rootPath: '/srv/edi', repoKey: 'github.com/jcoble/EdiPlatform', groupKey: 'repo:github.com/jcoble/EdiPlatform' }
];
const local = { cwd: '/code/assembly', projectPath: null, executionEnvironment: 'local', remoteProfileId: null, projectId: 'p2', projectGroupKey: 'repo:github.com/jcoble/mac-command-bar' };
assert.equal(sessionProjectFilter(local, projects), '/code/assembly');
assert.equal(sessionProjectFilter({ ...local, cwd: '/worktrees/tsk-1', projectId: null }, projects), '/code/assembly');
assert.equal(sessionProjectFilter({ ...local, cwd: '/code/test1234', projectId: 'p1', projectGroupKey: 'project:p1' }, projects), '');
assert.equal(sessionProjectFilter({ ...local, projectId: null, projectGroupKey: 'none' }, projects), '');
assert.equal(sessionProjectFilter({ ...local, cwd: '/srv/edi', executionEnvironment: 'remote', remoteProfileId: 'workbox', projectId: 'p3' }, projects), 'assembly-remote://workbox/srv/edi');
assert.equal(sessionProjectFilter(undefined, projects), '');

console.log('pullRequestSearch: ok');
