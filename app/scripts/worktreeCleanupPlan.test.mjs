import assert from 'node:assert/strict';

import { buildWorktreeCleanupPlan } from '../src/lib/worktreeCleanupPlan.ts';

const baseState = {
  repoName: 'MacCommandBar',
  repoRootPath: '/Users/blackcolours/dev/work/mac-command-bar',
  path: '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup',
  branch: 'cdx/tsk-42-cleanup',
  isMainRoot: false,
  isProtected: false,
  dirtyCount: 0,
  stagedCount: 0,
  untrackedCount: 0,
  aheadCount: 0,
  behindCount: 0,
  lastActivityAgeDays: 30,
  activeSessionCount: 0
};

function worktreeState(overrides = {}) {
  return {
    ...baseState,
    ...overrides
  };
}

{
  const plan = buildWorktreeCleanupPlan(worktreeState());

  assert.equal(plan.lane, 'safe-remove');
  assert.equal(plan.commandPlan.kind, 'clean-remove');
  assert.equal(plan.taskDisplay, null);
  assert.match(plan.explanation, /Clean and merged/);
  assert.match(plan.explanation, /30d inactive/);
  assert.deepEqual(plan.commandPlan.steps, [
    "git -C '/Users/blackcolours/dev/work/mac-command-bar' worktree remove '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup'",
    "git -C '/Users/blackcolours/dev/work/mac-command-bar' worktree prune"
  ]);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      dirtyCount: 2,
      stagedCount: 1,
      untrackedCount: 3,
      lastActivityAgeDays: 2
    })
  );

  assert.equal(plan.lane, 'backup-first');
  assert.equal(plan.commandPlan.kind, 'dirty-backup');
  assert.match(plan.explanation, /6 local changes/);
  assert.match(plan.explanation, /backup before removal/);
  assert.ok(plan.commandPlan.steps.includes("git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' branch 'backup/worktree-cleanup/cdx-tsk-42-cleanup'"));
  assert.ok(plan.commandPlan.steps.includes("git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' diff --binary > '/Users/blackcolours/dev/work/mac-command-bar/.worktree-cleanup-backups/tsk-42-cleanup-unstaged.patch'"));
  assert.ok(plan.commandPlan.steps.includes("git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' diff --cached --binary > '/Users/blackcolours/dev/work/mac-command-bar/.worktree-cleanup-backups/tsk-42-cleanup-staged.patch'"));
  assert.ok(plan.commandPlan.steps.includes("git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' ls-files --others --exclude-standard > '/Users/blackcolours/dev/work/mac-command-bar/.worktree-cleanup-backups/tsk-42-cleanup-untracked.txt'"));
  assert.ok(plan.commandPlan.steps.includes("git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' stash push --include-untracked -m 'worktree cleanup backup: cdx/tsk-42-cleanup'"));
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      activeSessionCount: 2,
      lastActivityAgeDays: 45
    })
  );

  assert.equal(plan.lane, 'blocked-active-session');
  assert.equal(plan.commandPlan.kind, 'active-session-warning');
  assert.match(plan.explanation, /2 active sessions/);
  assert.match(plan.explanation, /do not remove/);
  assert.deepEqual(plan.commandPlan.steps, [
    'Do not remove: 2 active sessions still point at this worktree.',
    "git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' status --short --branch",
    'Close, move, or resume those sessions before cleanup, then recompute the plan.'
  ]);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      path: '/Users/blackcolours/dev/work/mac-command-bar',
      branch: 'main',
      isMainRoot: true,
      isProtected: true,
      lastActivityAgeDays: 120
    })
  );

  assert.equal(plan.lane, 'blocked-protected');
  assert.equal(plan.commandPlan.kind, 'protected-keep');
  assert.match(plan.explanation, /Protected main checkout/);
  assert.deepEqual(plan.commandPlan.steps, [
    "Keep '/Users/blackcolours/dev/work/mac-command-bar' as the repository anchor.",
    "git -C '/Users/blackcolours/dev/work/mac-command-bar' worktree list",
    'Remove only sibling worktrees after their own cleanup plan says safe-remove.'
  ]);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      aheadCount: 3,
      hasUpstream: true,
      lastActivityAgeDays: 40
    })
  );

  assert.equal(plan.lane, 'review-first');
  assert.equal(plan.commandPlan.kind, 'branch-review');
  assert.match(plan.explanation, /3 commits ahead/);
  assert.match(plan.explanation, /review, push, or preserve/);
  assert.ok(plan.commandPlan.steps.includes("git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' log --oneline --decorate '@{upstream}..HEAD'"));
  assert.ok(plan.commandPlan.steps.includes("git -C '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-42-cleanup' push -u origin 'cdx/tsk-42-cleanup'"));
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      aheadCount: 2,
      hasUpstream: false,
      lastActivityAgeDays: 40
    })
  );
  const commandPlan = plan.commandPlan.steps.join('\n');

  assert.equal(plan.lane, 'review-first');
  assert.equal(plan.commandPlan.kind, 'branch-review');
  assert.match(commandPlan, /log --oneline --decorate --max-count=20 HEAD/);
  assert.match(commandPlan, /push -u origin 'cdx\/tsk-42-cleanup'/);
  assert.doesNotMatch(commandPlan, /@\{upstream\}/);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      aheadCount: 0,
      behindCount: 4,
      hasUpstream: true,
      lastActivityAgeDays: 40
    })
  );
  const commandPlan = plan.commandPlan.steps.join('\n');

  assert.equal(plan.lane, 'review-first');
  assert.equal(plan.commandPlan.kind, 'branch-review');
  assert.match(plan.explanation, /4 commits behind/);
  assert.match(commandPlan, /fetch origin/);
  assert.match(commandPlan, /HEAD\.\.@\{upstream\}/);
  assert.doesNotMatch(commandPlan, /push -u origin/);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      aheadCount: 2,
      behindCount: 4,
      hasUpstream: true,
      lastActivityAgeDays: 40
    })
  );
  const commandPlan = plan.commandPlan.steps.join('\n');

  assert.equal(plan.lane, 'review-first');
  assert.equal(plan.commandPlan.kind, 'branch-review');
  assert.match(plan.explanation, /2 commits ahead and 4 commits behind/);
  assert.match(commandPlan, /fetch origin/);
  assert.match(commandPlan, /--left-right --oneline --decorate 'HEAD\.\.\.@\{upstream\}'/);
  assert.match(commandPlan, /Resolve branch divergence/);
  assert.doesNotMatch(commandPlan, /push -u origin/);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      isLocked: true,
      lockedReason: 'manual safety lock',
      lastActivityAgeDays: 40
    })
  );
  const commandPlan = plan.commandPlan.steps.join('\n');

  assert.equal(plan.lane, 'blocked-locked');
  assert.equal(plan.commandPlan.kind, 'locked-warning');
  assert.match(plan.explanation, /Locked worktree: manual safety lock/);
  assert.match(commandPlan, /worktree list --porcelain/);
  assert.match(commandPlan, /Lock reason: manual safety lock/);
  assert.doesNotMatch(commandPlan, /worktree remove/);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      isPrunable: true,
      prunableReason: 'missing path',
      lastActivityAgeDays: 40
    })
  );
  const commandPlan = plan.commandPlan.steps.join('\n');

  assert.equal(plan.lane, 'review-prunable');
  assert.equal(plan.commandPlan.kind, 'prunable-review');
  assert.match(plan.explanation, /Missing worktree path: missing path/);
  assert.match(commandPlan, /worktree prune --dry-run/);
  assert.match(commandPlan, /Prunable reason: missing path/);
  assert.doesNotMatch(commandPlan, /worktree remove/);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      savedWorkspaceCount: 2,
      lastActivityAgeDays: 40
    })
  );
  const commandPlan = plan.commandPlan.steps.join('\n');

  assert.equal(plan.lane, 'review-saved-workspace');
  assert.equal(plan.commandPlan.kind, 'saved-workspace-review');
  assert.match(plan.explanation, /2 saved workspaces still point here/);
  assert.match(commandPlan, /Do not remove: 2 saved workspaces/);
  assert.doesNotMatch(commandPlan, /worktree remove/);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      deleteEligibility: 'requires-confirmation',
      lastActivityAgeDays: 40
    })
  );
  const commandPlan = plan.commandPlan.steps.join('\n');

  assert.equal(plan.lane, 'review-confirmation');
  assert.equal(plan.commandPlan.kind, 'confirmation-review');
  assert.match(plan.explanation, /requires-confirmation/);
  assert.match(commandPlan, /Confirm no active agent/);
  assert.doesNotMatch(commandPlan, /worktree remove/);
}

{
  const plan = buildWorktreeCleanupPlan(
    worktreeState({
      task: {
        id: 'TSK-42',
        url: 'https://notion.example.test/TSK-42',
        title: 'Clean stale worktrees'
      }
    })
  );

  assert.deepEqual(plan.taskDisplay, {
    id: 'TSK-42',
    label: 'TSK-42',
    href: 'https://notion.example.test/TSK-42',
    title: 'Clean stale worktrees'
  });
  assert.match(plan.explanation, /TSK-42/);
}
