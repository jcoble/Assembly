import assert from 'node:assert/strict';

import {
  buildGitTaskLink,
  buildGitTaskLinksFromText,
  buildGitTaskSearchTarget,
  buildGitTaskSearchTargetsFromText,
  extractGitTaskIDs,
  normalizeGitTaskID
} from '../src/lib/shell/git/gitTaskLinks.ts';

assert.equal(normalizeGitTaskID('TSK-127'), 'TSK-127');
assert.equal(normalizeGitTaskID('tsk-127'), 'TSK-127');
assert.equal(normalizeGitTaskID('[tsk-127]'), 'TSK-127');
assert.equal(normalizeGitTaskID('cdx/tsk-127-layout'), null);
assert.equal(normalizeGitTaskID('not-a-task'), null);

assert.deepEqual(extractGitTaskIDs('TSK-127'), ['TSK-127']);
assert.deepEqual(extractGitTaskIDs('ship [tsk-127] then TSK-128'), ['TSK-127', 'TSK-128']);
assert.deepEqual(extractGitTaskIDs('Task 127: route command bar links'), ['TSK-127']);
assert.deepEqual(extractGitTaskIDs('fix task #128 from review'), ['TSK-128']);
assert.deepEqual(extractGitTaskIDs('cdx/tsk-127-layout'), ['TSK-127']);
assert.deepEqual(extractGitTaskIDs('cdx/tsk-127-128-130-task-link-utility'), [
  'TSK-127',
  'TSK-128',
  'TSK-130'
]);
assert.deepEqual(extractGitTaskIDs('tsk-127 tsk-128 TSK-127'), ['TSK-127', 'TSK-128']);
assert.deepEqual(
  extractGitTaskIDs('TSK-127, tsk-128; cdx/tsk-129-layout [TSK-130]'),
  ['TSK-127', 'TSK-128', 'TSK-129', 'TSK-130']
);
assert.deepEqual(
  extractGitTaskIDs('cdx/tsk-127-layout polish\n\nRefs task 128 and TSK-127 again'),
  ['TSK-127', 'TSK-128']
);

assert.deepEqual(
  extractGitTaskIDs('xtsk-127 tsk-128x task_tsk-129 tsk-130_extra nottsk-131'),
  []
);
assert.deepEqual(extractGitTaskIDs('release 2026.06 issue #127 PR 128'), []);
assert.deepEqual(extractGitTaskIDs('subtask 127 tsk-130_extra xtsk-131'), []);
assert.deepEqual(extractGitTaskIDs('feature/tsk-127-layout fix: tsk-128.followup'), [
  'TSK-127',
  'TSK-128'
]);
assert.deepEqual(extractGitTaskIDs('refs/heads/cdx/TSK-131-command-center'), ['TSK-131']);
assert.deepEqual(extractGitTaskIDs('origin/cdx/tsk-132-git-cleanup HEAD -> cdx/TSK-133-audit'), [
  'TSK-132',
  'TSK-133'
]);

assert.deepEqual(buildGitTaskLink('tsk-127'), {
  id: 'TSK-127',
  label: 'TSK-127'
});
assert.deepEqual(buildGitTaskLink('not-a-task'), null);
assert.deepEqual(buildGitTaskLink('cdx/tsk-127-layout'), {
  id: 'TSK-127',
  label: 'TSK-127'
});
assert.deepEqual(buildGitTaskLink('refs/heads/cdx/TSK-130-layout'), {
  id: 'TSK-130',
  label: 'TSK-130'
});
assert.deepEqual(buildGitTaskLink('tsk-127', { baseURL: 'https://tasks.example.test/items' }), {
  id: 'TSK-127',
  label: 'TSK-127',
  href: 'https://tasks.example.test/items/TSK-127'
});
assert.deepEqual(buildGitTaskLink('TSK-128', { baseURL: 'https://tasks.example.test/items/' }), {
  id: 'TSK-128',
  label: 'TSK-128',
  href: 'https://tasks.example.test/items/TSK-128'
});
assert.deepEqual(buildGitTaskLink('TSK-129', { baseURL: '' }), {
  id: 'TSK-129',
  label: 'TSK-129'
});

assert.deepEqual(
  buildGitTaskLinksFromText(
    ['cdx/tsk-127-layout', 'feat: [TSK-128] TSK-127 cleanup'],
    { baseURL: 'https://tasks.example.test/tasks' }
  ),
  [
    {
      id: 'TSK-127',
      label: 'TSK-127',
      href: 'https://tasks.example.test/tasks/TSK-127'
    },
    {
      id: 'TSK-128',
      label: 'TSK-128',
      href: 'https://tasks.example.test/tasks/TSK-128'
    }
  ]
);

assert.deepEqual(buildGitTaskSearchTarget('task 127'), {
  kind: 'notion-search',
  id: 'TSK-127',
  label: 'TSK-127',
  query: 'TSK-127'
});
assert.deepEqual(buildGitTaskSearchTarget('not linked'), null);
assert.deepEqual(
  buildGitTaskSearchTargetsFromText(['task 127', 'cdx/tsk-128-129-links', '[TSK-127]']),
  [
    {
      kind: 'notion-search',
      id: 'TSK-127',
      label: 'TSK-127',
      query: 'TSK-127'
    },
    {
      kind: 'notion-search',
      id: 'TSK-128',
      label: 'TSK-128',
      query: 'TSK-128'
    },
    {
      kind: 'notion-search',
      id: 'TSK-129',
      label: 'TSK-129',
      query: 'TSK-129'
    }
  ]
);
