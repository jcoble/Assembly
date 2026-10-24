import assert from 'node:assert/strict';
import {
  buildWorktreeCleanupBrief,
  buildWorktreeCleanupScript,
  buildWorktreeDecisionQueue,
  buildWorktreeSafetySummary,
  buildWorktreeTaskGroups,
  prioritizeWorktreesForCleanup,
  worktreeDecisionLane,
  worktreePrimaryAction,
  worktreeAuditCommand,
  worktreeBackupCommand,
  worktreeCleanupCommand
} from '../src/lib/worktreeSafety.ts';

const now = new Date('2026-06-10T12:00:00.000Z');

function worktree(overrides = {}) {
  return {
    repo: 'EdiPlatform',
    path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center',
    branch: 'cdx/tsk-127-command-center',
    taskID: 'TSK-127',
    isDirty: false,
    hasUnmergedCommits: false,
    lastActivity: '2026-06-09T12:00:00.000Z',
    deleteEligibility: 'requires-confirmation',
    ...overrides
  };
}

{
  const summary = buildWorktreeSafetySummary(worktree({ path: '/repo/main', branch: 'main' }), {
    primaryPath: '/repo/main/',
    now
  });
  assert.equal(summary.kind, 'protected');
  assert.equal(summary.badge, 'Main');
  assert.deepEqual(summary.decisionChecklist, [
    'Keep this checkout as the repository anchor.',
    'Clean sibling worktrees instead of removing main.'
  ]);
  assert.match(summary.cleanupPlan, /Decision checklist:/);
  assert.match(summary.cleanupPlan, /Do not remove the primary checkout/);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Keep');
  assert.equal(lane.tone, 'protected');
  assert.match(lane.detail, /repo anchor/);
}

{
  const summary = buildWorktreeSafetySummary(worktree({ isDirty: true }), {
    primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
    now
  });
  assert.equal(summary.kind, 'blocked');
  assert.equal(summary.badge, 'Dirty');
  assert.match(summary.reason, /Uncommitted/);
  assert.match(summary.auditCommand, /status --short --branch/);
  assert.match(summary.backupCommand, /stash push --include-untracked/);
  assert.match(summary.backupCommand, /worktree-archives\/EdiPlatform\//);
  assert.match(summary.backupCommand, /status --short --branch > .*status\.txt/);
  assert.match(summary.backupCommand, /log --oneline --decorate --max-count=40 > .*commits\.txt/);
  assert.match(summary.backupCommand, /diff --binary > .*unstaged\.patch/);
  assert.match(summary.backupCommand, /diff --cached --binary > .*staged\.patch/);
  assert.match(summary.backupCommand, /ls-files --others --exclude-standard > .*untracked\.txt/);
  assert.match(summary.backupCommand, /bundle create .*head\.bundle' HEAD/);
  assert.match(summary.cleanupPlan, /Audit before cleanup/);
  assert.match(summary.cleanupPlan, /Archive a recoverable backup/);
  assert.deepEqual(summary.decisionChecklist, [
    'Inspect git status and uncommitted files.',
    'Archive, commit, or stash the changes before removal.',
    'Remove only after the worktree is clean or intentionally backed up.'
  ]);
  assert.match(summary.cleanupPlan, /Inspect git status and uncommitted files/);

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'backup');
  assert.equal(action.label, 'Backup');
  assert.equal(action.command, summary.backupCommand);
  assert.match(action.title, /Archive/);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Backup');
  assert.equal(lane.tone, 'backup');
  assert.match(lane.detail, /Uncommitted changes/);
}

{
  const summary = buildWorktreeSafetySummary(
    worktree({
      isDirty: false,
      deleteEligibility: 'blocked: untracked files'
    }),
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now
    }
  );
  assert.equal(summary.kind, 'blocked');
  assert.equal(summary.badge, 'Untracked');
  assert.match(summary.reason, /Untracked files/);
  assert.deepEqual(summary.decisionChecklist, [
    'Inspect untracked files before cleanup.',
    'Archive, commit, or intentionally discard them before removal.',
    'Remove only after those files are recoverable or confirmed unnecessary.'
  ]);

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'backup');

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Backup files');
  assert.equal(lane.tone, 'backup');
  assert.match(lane.detail, /Untracked files/);
}

{
  const summary = buildWorktreeSafetySummary(worktree(), {
    primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
    activeSessionPaths: [
      '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center/ediplatform-web'
    ],
    now
  });
  assert.equal(summary.kind, 'blocked');
  assert.equal(summary.badge, 'Active');
  assert.equal(summary.activeSessionCount, 1);
  assert.match(summary.reason, /Active session/);
  assert.deepEqual(summary.decisionChecklist, [
    'Resume or close the active sessions using this path.',
    'Refresh sessions and worktrees before attempting cleanup.'
  ]);
  assert.match(summary.cleanupPlan, /Do not remove while active sessions point here/);

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'audit');
  assert.equal(action.command, summary.auditCommand);
  assert.match(action.title, /active/);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Active');
  assert.equal(lane.tone, 'blocked');
  assert.match(lane.detail, /Active sessions/);
}

{
  const summary = buildWorktreeSafetySummary(worktree({ hasUnmergedCommits: true }), {
    primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
    now
  });
  assert.equal(summary.kind, 'blocked');
  assert.equal(summary.badge, 'Unmerged');
  assert.match(summary.recommendation, /push/);
  assert.deepEqual(summary.decisionChecklist, [
    'Inspect local commits that are not on a remote branch.',
    'Push, merge, cherry-pick, or archive the branch before removal.',
    'Remove only after the branch is recoverable from another ref.'
  ]);
  assert.match(summary.cleanupPlan, /Inspect local commits/);

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'backup');
  assert.equal(action.command, summary.backupCommand);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Save commits');
  assert.equal(lane.tone, 'backup');
  assert.match(lane.detail, /Local commits/);
}

{
  const summary = buildWorktreeSafetySummary(
    worktree({ lastActivity: '2026-05-20T12:00:00.000Z' }),
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now,
      staleAfterDays: 14
    }
  );
  assert.equal(summary.kind, 'ready');
  assert.equal(summary.badge, 'Stale');
  assert.equal(summary.ageBucket, 'stale');
  assert.match(summary.recommendation, /cleanup candidate/);
  assert.deepEqual(summary.decisionChecklist, [
    'Confirm no active session owns this stale path.',
    'Remove from the main checkout, then prune worktree metadata.'
  ]);

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'cleanup');
  assert.equal(action.label, 'Remove');
  assert.equal(action.command, summary.cleanupCommand);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Stale clean');
  assert.equal(lane.tone, 'cleanup');
  assert.match(lane.detail, /Clean worktree/);
}

{
  const summary = buildWorktreeSafetySummary(worktree({ lastActivity: null }), {
    primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
    now
  });
  assert.equal(summary.kind, 'review');
  assert.equal(summary.badge, 'Review');
  assert.equal(summary.activityLabel, 'activity unknown');

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'audit');
  assert.equal(action.command, summary.auditCommand);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Review');
  assert.equal(lane.tone, 'review');
  assert.match(lane.detail, /Confirm ownership/);
}

{
  const summary = buildWorktreeSafetySummary(
    worktree({
      path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-147-saved-workspace',
      branch: 'cdx/tsk-147-saved-workspace',
      taskID: null,
      lastActivity: '2026-05-20T12:00:00.000Z',
      deleteEligibility: 'requires-confirmation'
    }),
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      savedWorkspacePaths: [
        '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-147-saved-workspace',
        '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-147-saved-workspace/src/routes'
      ],
      now,
      staleAfterDays: 14
    }
  );
  assert.equal(summary.kind, 'review');
  assert.equal(summary.badge, 'Workspace');
  assert.equal(summary.savedWorkspaceCount, 2);
  assert.equal(summary.ageBucket, 'stale');
  assert.match(summary.reason, /Saved workspace/);
  assert.match(summary.recommendation, /saved workspace/);
  assert.match(summary.cleanupPlan, /Saved workspaces: 2/);
  assert.match(summary.cleanupPlan, /Do not remove while saved workspace snapshots still point here/);
  assert.match(summary.cleanupPlan, /Task: TSK-147/);
  assert.deepEqual(summary.decisionChecklist, [
    'Review saved workspace snapshots that point at this path.',
    'Restore, move, or delete stale workspace snapshots before removal.',
    'Refresh worktrees after clearing saved workspace ownership.'
  ]);

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'audit');

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Workspace');
  assert.equal(lane.tone, 'review');
  assert.match(lane.detail, /Saved workspace/);
}

{
  const summary = buildWorktreeSafetySummary(
    worktree({
      path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/missing-task',
      branch: 'cdx/tsk-145-missing',
      taskID: 'TSK-145',
      isPrunable: true,
      prunableReason: 'gitdir file points to non-existent location',
      lastActivity: null,
      deleteEligibility: 'review: prunable missing worktree metadata'
    }),
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now
    }
  );
  assert.equal(summary.kind, 'review');
  assert.equal(summary.badge, 'Missing');
  assert.match(summary.reason, /Missing worktree path/);
  assert.match(summary.auditCommand, /worktree list --porcelain/);
  assert.match(summary.auditCommand, /worktree prune --dry-run --verbose/);
  assert.doesNotMatch(summary.auditCommand, /git -C '\/Users\/blackcolours\/dev\/work\/worktrees\/EdiPlatform\/missing-task'/);
  assert.match(summary.backupCommand, /Missing\/prunable worktree path has no files to archive/);
  assert.equal(
    summary.cleanupCommand,
    "git -C '/Users/blackcolours/dev/work/EdiPlatform' worktree prune"
  );
  assert.deepEqual(summary.decisionChecklist, [
    'Confirm the path is intentionally gone and not a disconnected volume.',
    'Run a dry-run prune from the main checkout.',
    'Prune stale metadata only after confirmation.'
  ]);
  assert.match(summary.cleanupPlan, /Metadata-only cleanup/);
  assert.match(summary.cleanupPlan, /does not delete source files/);

  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'audit');
  assert.equal(action.command, summary.auditCommand);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Prune');
  assert.equal(lane.tone, 'review');
  assert.match(lane.detail, /Missing path/);
}

{
  const summary = buildWorktreeSafetySummary(
    worktree({
      branch: 'cdx/tsk-146-locked',
      isLocked: true,
      lockedReason: 'agent still running',
      deleteEligibility: 'blocked: locked worktree'
    }),
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now
    }
  );
  assert.equal(summary.kind, 'blocked');
  assert.equal(summary.badge, 'Locked');
  assert.match(summary.recommendation, /unlock intentionally/);
  assert.deepEqual(summary.decisionChecklist, [
    'Inspect the Git worktree lock reason.',
    'Unlock only when you know no external process owns this worktree.',
    'Refresh worktrees before attempting cleanup.'
  ]);
  assert.match(summary.cleanupPlan, /Do not remove locked worktrees/);

  const lane = worktreeDecisionLane(summary);
  assert.equal(lane.label, 'Locked');
  assert.equal(lane.tone, 'blocked');
  assert.match(lane.detail, /unlock/);
}

{
  const summary = buildWorktreeSafetySummary(worktree({ path: '/repo/main', branch: 'main' }), {
    primaryPath: '/repo/main/',
    now
  });
  const action = worktreePrimaryAction(summary);
  assert.equal(action.kind, 'audit');
  assert.equal(action.label, 'Audit');
  assert.equal(action.command, summary.auditCommand);
}

{
  const command = worktreeAuditCommand(worktree({ path: "/tmp/audit ' quote" }));
  assert.match(command, /git -C '\/tmp\/audit '\\'' quote' status --short --branch/);
  assert.match(command, /branch -vv/);
  assert.match(command, /log --oneline --decorate --max-count=8/);
  assert.match(command, /log --branches --not --remotes --oneline --decorate --max-count=20/);
}

{
  const command = worktreeCleanupCommand(
    worktree({ path: "/tmp/has ' quote" }),
    '/Users/blackcolours/dev/work/EdiPlatform'
  );
  assert.match(command, /git -C '\/Users\/blackcolours\/dev\/work\/EdiPlatform' worktree remove/);
  assert.match(command, /'\/tmp\/has '\\'' quote'/);
  assert.match(command, /worktree prune/);
}

{
  const command = worktreeBackupCommand(
    worktree({ path: "/Users/blackcolours/dev/work/worktrees/EdiPlatform/has ' quote" }),
    now
  );
  assert.match(command, /mkdir -p/);
  assert.match(command, /worktree-archives\/EdiPlatform\/EdiPlatform-cdx-tsk-127-command-center-2026-06-10T12-00-00-000Z/);
  assert.match(command, /'\/Users\/blackcolours\/dev\/work\/worktrees\/EdiPlatform\/has '\\'' quote'/);
  assert.match(command, /stash push --include-untracked/);
}

{
  const brief = buildWorktreeCleanupBrief(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/EdiPlatform',
        branch: 'main',
        taskID: null
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-120-clean',
        branch: 'cdx/tsk-120-clean',
        taskID: 'TSK-120',
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-121-dirty',
        branch: 'cdx/tsk-121-dirty',
        taskID: 'TSK-121',
        isDirty: true
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-122-review',
        branch: 'cdx/tsk-122-review',
        taskID: 'TSK-122',
        lastActivity: null
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      activeSessionPaths: [
        '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-120-clean'
      ],
      now,
      staleAfterDays: 14
    }
  );

  assert.equal(brief.headline, '2 blocked · 1 review · 1 stale · 1 main');
  assert.equal(brief.cleanupCandidateCount, 0);
  assert.deepEqual(brief.taskIDs, ['TSK-120', 'TSK-121', 'TSK-122']);
  assert.match(brief.report, /Worktree cleanup brief/);
  assert.match(brief.report, /Blocked: 2/);
  assert.match(brief.report, /Cleanup candidates:/);
  assert.match(brief.report, /cdx\/tsk-120-clean/);
  assert.match(brief.report, /Needs attention:/);
  assert.match(brief.report, /cdx\/tsk-121-dirty/);
  assert.match(brief.report, /Review:/);
  assert.match(brief.report, /cdx\/tsk-122-review/);
}

{
  const brief = buildWorktreeCleanupBrief(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-160-branch-only',
        branch: 'cdx/tsk-160-branch-only',
        taskID: null,
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-161-path-only',
        branch: 'feature/no-task-name',
        taskID: null,
        lastActivity: '2026-05-20T12:00:00.000Z'
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now,
      staleAfterDays: 14
    }
  );

  assert.deepEqual(brief.taskIDs, ['TSK-160', 'TSK-161']);
  assert.match(brief.report, /Tasks: TSK-160, TSK-161/);
}

{
  const ordered = prioritizeWorktreesForCleanup(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/EdiPlatform',
        branch: 'main',
        taskID: null
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-140-review',
        branch: 'cdx/tsk-140-review',
        taskID: 'TSK-140',
        lastActivity: null
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-141-clean',
        branch: 'cdx/tsk-141-clean',
        taskID: 'TSK-141',
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-142-dirty',
        branch: 'cdx/tsk-142-dirty',
        taskID: 'TSK-142',
        isDirty: true
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-143-active',
        branch: 'cdx/tsk-143-active',
        taskID: 'TSK-143'
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      activeSessionPaths: [
        '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-143-active/.codex/session'
      ],
      now,
      staleAfterDays: 14
    }
  );

  assert.deepEqual(
    ordered.map((entry) => entry.branch),
    [
      'cdx/tsk-143-active',
      'cdx/tsk-142-dirty',
      'cdx/tsk-141-clean',
      'cdx/tsk-140-review',
      'main'
    ]
  );
}

{
  const queue = buildWorktreeDecisionQueue(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/EdiPlatform',
        branch: 'main',
        taskID: null
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-141-clean',
        branch: 'cdx/tsk-141-clean',
        taskID: 'TSK-141',
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-142-dirty',
        branch: 'cdx/tsk-142-dirty',
        taskID: 'TSK-142',
        isDirty: true
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-143-active',
        branch: 'cdx/tsk-143-active',
        taskID: 'TSK-143'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-144-review',
        branch: 'cdx/tsk-144-review',
        taskID: 'TSK-144',
        lastActivity: null
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      activeSessionPaths: [
        '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-143-active/.codex/session'
      ],
      now,
      staleAfterDays: 14
    }
  );

  assert.deepEqual(
    queue.map((group) => group.id),
    ['blocked', 'ready', 'review', 'protected']
  );
  assert.equal(queue[0].label, 'Needs decision');
  assert.match(queue[0].summary, /2 worktrees/);
  assert.match(queue[0].summary, /1 active session/);
  assert.deepEqual(
    queue[0].entries.map((entry) => entry.primaryAction.kind),
    ['audit', 'backup']
  );
  assert.equal(queue[1].label, 'Cleanup ready');
  assert.equal(queue[1].entries[0].primaryAction.kind, 'cleanup');
  assert.match(queue[1].summary, /1 stale/);
  assert.equal(queue[2].entries[0].primaryAction.kind, 'audit');
  assert.equal(queue[3].label, 'Protected');
}

{
  const groups = buildWorktreeTaskGroups(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-162-branch-derived',
        branch: 'cdx/tsk-162-branch-derived',
        taskID: null,
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-162-dirty',
        branch: 'cdx/no-task-here',
        taskID: 'tsk-162',
        isDirty: true
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now,
      staleAfterDays: 14
    }
  );

  assert.deepEqual(
    groups.map((group) => group.label),
    ['TSK-162']
  );
  assert.equal(groups[0].worktreeCount, 2);
  assert.deepEqual(
    groups[0].entries.map((entry) => entry.worktree.branch),
    ['cdx/no-task-here', 'cdx/tsk-162-branch-derived']
  );
}

{
  const groups = buildWorktreeTaskGroups(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-151-clean-a',
        branch: 'cdx/tsk-151-clean-a',
        taskID: ' TSK-151 ',
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-151-dirty-b',
        branch: 'cdx/tsk-151-dirty-b',
        taskID: 'TSK-151',
        isDirty: true
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-152-clean',
        branch: 'cdx/tsk-152-clean',
        taskID: 'TSK-152',
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/no-task-review',
        branch: 'cdx/no-task-review',
        taskID: '  ',
        lastActivity: null
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now,
      staleAfterDays: 14
    }
  );

  assert.deepEqual(
    groups.map((group) => group.label),
    ['TSK-151', 'TSK-152', 'No task ID']
  );

  const mixedTask = groups[0];
  assert.equal(mixedTask.taskID, 'TSK-151');
  assert.equal(mixedTask.worktreeCount, 2);
  assert.equal(mixedTask.blockedCount, 1);
  assert.equal(mixedTask.cleanupCandidateCount, 1);
  assert.equal(mixedTask.needsBackupCount, 1);
  assert.equal(mixedTask.staleCount, 1);
  assert.equal(mixedTask.requiresManualSignoff, true);
  assert.equal(mixedTask.primaryAction.kind, 'backup');
  assert.match(mixedTask.summary, /2 worktrees/);
  assert.match(mixedTask.summary, /1 blocked/);
  assert.match(mixedTask.summary, /1 need backup/);
  assert.match(mixedTask.summary, /1 cleanup ready/);
  assert.deepEqual(
    mixedTask.entries.map((entry) => entry.worktree.branch),
    ['cdx/tsk-151-dirty-b', 'cdx/tsk-151-clean-a']
  );

  assert.equal(groups[1].requiresManualSignoff, false);
  assert.equal(groups[1].primaryAction.kind, 'cleanup');
  assert.equal(groups[2].taskID, null);
  assert.equal(groups[2].reviewCount, 1);
  assert.equal(groups[2].requiresManualSignoff, true);
  assert.equal(groups[2].primaryAction.kind, 'audit');
}

{
  const groups = buildWorktreeTaskGroups(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-153-clean',
        branch: 'cdx/tsk-153-clean',
        taskID: 'tsk-153',
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-153-missing',
        branch: 'cdx/tsk-153-missing',
        taskID: 'TSK-153',
        isPrunable: true,
        lastActivity: null,
        deleteEligibility: 'review: prunable missing worktree metadata'
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now,
      staleAfterDays: 14
    }
  );

  assert.deepEqual(
    groups.map((group) => group.label),
    ['TSK-153']
  );

  const taskGroup = groups[0];
  assert.equal(taskGroup.taskID, 'TSK-153');
  assert.equal(taskGroup.worktreeCount, 2);
  assert.equal(taskGroup.reviewCount, 1);
  assert.equal(taskGroup.cleanupCandidateCount, 1);
  assert.equal(taskGroup.staleCount, 1);
  assert.equal(taskGroup.requiresManualSignoff, true);
  assert.equal(taskGroup.primaryAction.kind, 'audit');
  assert.match(taskGroup.summary, /1 cleanup ready/);
  assert.match(taskGroup.summary, /1 review/);
  assert.deepEqual(
    taskGroup.entries.map((entry) => entry.worktree.branch),
    ['cdx/tsk-153-missing', 'cdx/tsk-153-clean']
  );
}

{
  const script = buildWorktreeCleanupScript(
    [
      worktree({
        path: '/Users/blackcolours/dev/work/EdiPlatform',
        branch: 'main',
        taskID: null
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-120-clean',
        branch: 'cdx/tsk-120-clean',
        taskID: 'TSK-120',
        lastActivity: '2026-05-20T12:00:00.000Z'
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-121-dirty',
        branch: 'cdx/tsk-121-dirty',
        taskID: 'TSK-121',
        isDirty: true
      }),
      worktree({
        path: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-122-missing',
        branch: 'cdx/tsk-122-missing',
        taskID: 'TSK-122',
        isPrunable: true,
        lastActivity: null,
        deleteEligibility: 'review: prunable missing worktree metadata'
      })
    ],
    {
      primaryPath: '/Users/blackcolours/dev/work/EdiPlatform',
      now,
      staleAfterDays: 14
    }
  );

  assert.match(script, /^#!\/usr\/bin\/env bash/);
  assert.match(script, /RUN_BACKUP="\$\{RUN_BACKUP:-0\}"/);
  assert.match(script, /RUN_REMOVE="\$\{RUN_REMOVE:-0\}"/);
  assert.match(script, /Audit cdx\/tsk-121-dirty/);
  assert.match(script, /Backup cdx\/tsk-121-dirty/);
  assert.match(script, /Forced dirty\/unmerged worktree removal is intentionally not generated/);
  assert.match(script, /Remove cdx\/tsk-120-clean/);
  assert.match(script, /Prune cdx\/tsk-122-missing: set RUN_REMOVE=1 to execute metadata cleanup/);
  assert.match(script, /worktree prune/);
  assert.doesNotMatch(script, /worktree remove '\/Users\/blackcolours\/dev\/work\/EdiPlatform'/);
  assert.doesNotMatch(script, /worktree remove --force/);
  assert.match(script, /echo "Dry run complete/);
}
