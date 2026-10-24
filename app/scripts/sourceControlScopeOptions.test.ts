/**
 * sourceControlScopeOptions.test.ts — only the session's own folder is called
 * "(session)" in the Source control folder picker. A project the session is
 * only viewing is a different folder, so it is listed by its plain name.
 */
const assert = (await import('node:assert/strict')).default;
const { sourceControlScopeOptions } = await import(
  '../src/lib/shell/git/components/sourceControl/sourceControlPanelActions.ts'
);

const worktrees = [
  { path: '/projects/proj-a', branch: 'main' },
  { path: '/projects/proj-a-tsk-1', branch: 'tsk-1-thing' }
];

const own = sourceControlScopeOptions('/projects/proj-a', worktrees, false);
assert.equal(own[0].label, 'proj-a (session)', "the session's own folder says so");

const viewed = sourceControlScopeOptions('/projects/proj-a', worktrees, true);
assert.deepEqual(
  viewed.map((option) => option.path),
  ['/projects/proj-a', '/projects/proj-a-tsk-1'],
  'a viewed project still lists itself first, then its other checkouts'
);
assert.ok(
  viewed.every((option) => !option.label.includes('(session)')),
  'no folder of a viewed project is called the session folder'
);
assert.equal(viewed[0].label, 'proj-a', 'the viewed folder shows its plain name');

console.log('sourceControlScopeOptions: ok');
