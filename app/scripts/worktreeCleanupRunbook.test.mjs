import assert from 'node:assert/strict';

import { buildWorktreeCleanupPlan } from '../src/lib/worktreeCleanupPlan.ts';
import { buildWorktreeCleanupRunbook } from '../src/lib/worktreeCleanupRunbook.ts';
import {
  buildWorktreeDecisionQueue,
  buildWorktreeSafetySummary
} from '../src/lib/worktreeSafety.ts';

const repoRootPath = '/Users/blackcolours/dev/work/mac-command-bar';
const worktreeRoot = '/Users/blackcolours/dev/work/worktrees/mac-command-bar';
const now = new Date('2026-06-15T12:00:00.000Z');

const baseCleanupState = {
  repoName: 'MacCommandBar',
  repoRootPath,
  path: `${worktreeRoot}/tsk-401-clean`,
  branch: 'cdx/tsk-401-clean',
  isMainRoot: false,
  isProtected: false,
  dirtyCount: 0,
  stagedCount: 0,
  untrackedCount: 0,
  aheadCount: 0,
  behindCount: 0,
  hasUpstream: true,
  lastActivityAgeDays: 30,
  activeSessionCount: 0,
  savedWorkspaceCount: 0,
  isLocked: false,
  isPrunable: false,
  deleteEligibility: 'safe'
};

function cleanupState(overrides = {}) {
  return {
    ...baseCleanupState,
    ...overrides
  };
}

function projectWorktree(overrides = {}) {
  return {
    repo: 'MacCommandBar',
    path: `${worktreeRoot}/tsk-401-clean`,
    branch: 'cdx/tsk-401-clean',
    taskID: 'TSK-401',
    isDirty: false,
    hasUnmergedCommits: false,
    lastActivity: '2026-05-10T12:00:00.000Z',
    deleteEligibility: 'safe',
    ...overrides
  };
}

{
  const plan = buildWorktreeCleanupPlan(
    cleanupState({
      task: {
        id: 'TSK-401',
        title: 'Clean stale worktrees'
      }
    })
  );
  const decisionQueue = buildWorktreeDecisionQueue([projectWorktree()], {
    primaryPath: repoRootPath,
    now,
    staleAfterDays: 14
  });
  const runbook = buildWorktreeCleanupRunbook({
    cleanupPlans: [plan],
    decisionQueue
  });

  assert.equal(runbook.headline, '1 worktree · 1 safe/removable · 0 backup required · 0 review · 0 blocked · 0 protected/active · 0 saved workspace/review');
  assert.equal(runbook.counts.safeRemovable, 1);
  assert.equal(runbook.counts.commandBlocks, 1);
  assert.deepEqual(runbook.lanes.map((group) => group.id), ['safe-remove']);
  assert.equal(runbook.lanes[0].label, 'Safe remove');
  assert.equal(runbook.tasks[0].label, 'TSK-401');
  assert.equal(runbook.states[0].label, 'Clean stale');

  const [item] = runbook.items;
  assert.equal(item.lane, 'safe-remove');
  assert.equal(item.decisionQueueID, 'ready');
  assert.equal(item.source.hasCleanupPlan, true);
  assert.equal(item.source.hasDecisionQueue, true);
  assert.match(item.commandBlocks[0].copyText, /worktree remove/);
  assert.match(item.commandBlocks[0].copyText, /worktree prune/);
  assert.match(item.copyText, /Safe remove: cdx\/tsk-401-clean/);
  assert.match(runbook.copyText, /Safe remove \(1\)/);
}

{
  const plan = buildWorktreeCleanupPlan(
    cleanupState({
      path: `${worktreeRoot}/tsk-402-dirty`,
      branch: 'cdx/tsk-402-dirty',
      dirtyCount: 2,
      stagedCount: 1,
      untrackedCount: 1,
      lastActivityAgeDays: 2,
      task: {
        id: 'TSK-402'
      }
    })
  );
  const safety = buildWorktreeSafetySummary(
    projectWorktree({
      path: `${worktreeRoot}/tsk-402-dirty`,
      branch: 'cdx/tsk-402-dirty',
      taskID: 'TSK-402',
      isDirty: true,
      lastActivity: '2026-06-14T12:00:00.000Z'
    }),
    {
      primaryPath: repoRootPath,
      now,
      staleAfterDays: 14
    }
  );
  const runbook = buildWorktreeCleanupRunbook({
    cleanupPlans: [plan],
    safetySummaries: [
      {
        path: plan.facts.path,
        repoName: 'MacCommandBar',
        branch: plan.facts.branch,
        taskID: 'TSK-402',
        safety
      }
    ]
  });

  assert.equal(runbook.counts.backupRequired, 1);
  assert.equal(runbook.counts.safeRemovable, 0);
  assert.deepEqual(runbook.lanes.map((group) => group.id), ['backup-first']);

  const [item] = runbook.items;
  assert.equal(item.laneLabel, 'Backup first');
  assert.equal(item.stateID, 'dirty-local-changes');
  assert.equal(item.safetyBadge, 'Dirty');
  assert.equal(item.source.hasSafetySummary, true);
  assert.match(item.explanation, /backup before removal/);
  assert.match(item.commandBlocks[0].copyText, /stash push --include-untracked/);
  assert.doesNotMatch(item.commandBlocks[0].copyText, /worktree remove/);
}

{
  const activePlan = buildWorktreeCleanupPlan(
    cleanupState({
      path: `${worktreeRoot}/tsk-403-active`,
      branch: 'cdx/tsk-403-active',
      activeSessionCount: 1,
      lastActivityAgeDays: 45,
      task: {
        id: 'TSK-403'
      }
    })
  );
  const protectedPlan = buildWorktreeCleanupPlan(
    cleanupState({
      path: repoRootPath,
      branch: 'main',
      isMainRoot: true,
      isProtected: true,
      lastActivityAgeDays: 120,
      task: null
    })
  );
  const decisionQueue = buildWorktreeDecisionQueue(
    [
      projectWorktree({
        path: `${worktreeRoot}/tsk-403-active`,
        branch: 'cdx/tsk-403-active',
        taskID: 'TSK-403',
        lastActivity: '2026-05-01T12:00:00.000Z'
      }),
      projectWorktree({
        path: repoRootPath,
        branch: 'main',
        taskID: null,
        lastActivity: '2026-01-01T12:00:00.000Z'
      })
    ],
    {
      primaryPath: repoRootPath,
      activeSessionPaths: [`${worktreeRoot}/tsk-403-active/.codex/session`],
      now,
      staleAfterDays: 14
    }
  );
  const runbook = buildWorktreeCleanupRunbook({
    cleanupPlans: [activePlan, protectedPlan],
    decisionQueue
  });

  assert.equal(runbook.counts.blocked, 2);
  assert.equal(runbook.counts.protectedOrActive, 2);
  assert.equal(runbook.counts.activeSessionBlocked, 1);
  assert.equal(runbook.counts.protected, 1);
  assert.deepEqual(
    runbook.lanes.map((group) => group.label),
    ['Blocked: active session', 'Protected checkout']
  );

  const activeItem = runbook.items.find((item) => item.lane === 'blocked-active-session');
  const protectedItem = runbook.items.find((item) => item.lane === 'blocked-protected');

  assert.ok(activeItem);
  assert.ok(protectedItem);
  assert.equal(activeItem.stateLabel, 'Active session');
  assert.equal(activeItem.decisionQueueID, 'blocked');
  assert.match(activeItem.commandBlocks[0].copyText, /Do not remove: 1 active session/);
  assert.equal(protectedItem.stateLabel, 'Protected checkout');
  assert.equal(protectedItem.decisionQueueID, 'protected');
  assert.match(protectedItem.commandBlocks[0].copyText, /repository anchor/);
  assert.doesNotMatch(protectedItem.commandBlocks[0].copyText, /worktree remove/);
}

{
  const plan = buildWorktreeCleanupPlan(
    cleanupState({
      path: `${worktreeRoot}/tsk-404-saved-workspace`,
      branch: 'cdx/tsk-404-saved-workspace',
      savedWorkspaceCount: 2,
      lastActivityAgeDays: 60,
      task: {
        id: 'TSK-404',
        title: 'Saved workspace cleanup'
      }
    })
  );
  const runbook = buildWorktreeCleanupRunbook({
    cleanupPlans: [plan]
  });

  assert.equal(runbook.counts.reviewRequired, 1);
  assert.equal(runbook.counts.savedWorkspaceReview, 1);
  assert.equal(runbook.counts.blocked, 0);
  assert.deepEqual(runbook.lanes.map((group) => group.id), ['review-saved-workspace']);
  assert.equal(runbook.lanes[0].label, 'Saved workspace review');
  assert.equal(runbook.states[0].label, 'Saved workspace');
  assert.match(runbook.headline, /1 saved workspace\/review/);

  const [item] = runbook.items;
  assert.equal(item.lane, 'review-saved-workspace');
  assert.equal(item.savedWorkspaceCount, 2);
  assert.match(item.commandBlocks[0].copyText, /Do not remove: 2 saved workspaces/);
  assert.match(item.copyText, /Saved workspace review/);
  assert.doesNotMatch(item.commandBlocks[0].copyText, /worktree remove/);
}
