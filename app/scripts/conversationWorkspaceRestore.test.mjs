// import assert from 'node:assert/strict';
// import { planConversationRestore } from '../src/lib/conversationWorkspaceRestore.ts';

// const worktreePath = '/Users/blackcolours/dev/work/worktrees/mac-command-bar/tsk-346-live';
// const projectPath = '/Users/blackcolours/dev/work/mac-command-bar';
// const resumeCommand = 'codex resume 019c-session';

// // (a) worktree present and in knownWorktreePaths -> cwd === worktreePath, missingWorktree false.
// const present = planConversationRestore({
//   session: { projectPath, branchHint: null },
//   snapshot: { worktreePath, branch: 'cdx/tsk-346-live', cwd: '/tmp/elsewhere' },
//   knownWorktreePaths: [`${worktreePath}/`, '/other/worktree'],
//   resumeCommand
// });
// assert.equal(present.cwd, worktreePath, 'worktree should win as the restore cwd');
// assert.equal(present.worktreePath, worktreePath);
// assert.equal(present.missingWorktree, false, 'a registered worktree is not missing');
// assert.equal(present.branch, 'cdx/tsk-346-live', 'branchHint wins over snapshot branch');
// assert.deepEqual(present.preCommands, []);
// assert.equal(present.resumeCommand, resumeCommand, 'resume command passes through unchanged');

// // (b) worktree set but absent from knownWorktreePaths -> missingWorktree true, preCommands [].
// const missing = planConversationRestore({
//   session: { projectPath },
//   snapshot: { worktreePath },
//   knownWorktreePaths: ['/some/other/worktree'],
//   resumeCommand
// });
// assert.equal(missing.worktreePath, worktreePath);
// assert.equal(missing.cwd, worktreePath, 'cwd still points at the (missing) worktree');
// assert.equal(missing.missingWorktree, true, 'unregistered worktree is flagged missing');
// assert.deepEqual(missing.preCommands, [], 'missing worktree is never auto-repaired with commands');

// // An empty known-worktree list still flags a set worktree as missing.
// const missingEmptyKnown = planConversationRestore({
//   session: {},
//   snapshot: { worktreePath },
//   knownWorktreePaths: [],
//   resumeCommand
// });
// assert.equal(missingEmptyKnown.missingWorktree, true, 'no known worktrees means a set worktree is missing');
// assert.deepEqual(missingEmptyKnown.preCommands, []);

// // (c) no worktree, projectPath set -> cwd === projectPath.
// const projectOnly = planConversationRestore({
//   session: { projectPath },
//   snapshot: { worktreePath: null, cwd: '/tmp/snapshot-cwd' },
//   knownWorktreePaths: [],
//   resumeCommand
// });
// assert.equal(projectOnly.cwd, projectPath, 'project path is the cwd when no worktree is saved');
// assert.equal(projectOnly.worktreePath, null);
// assert.equal(projectOnly.missingWorktree, false, 'no worktree means nothing can be missing');
// assert.deepEqual(projectOnly.preCommands, []);

// // no worktree, no projectPath -> falls back to snapshot.cwd.
// const snapshotCwdOnly = planConversationRestore({
//   session: {},
//   snapshot: { cwd: '/tmp/snapshot-cwd' },
//   knownWorktreePaths: [],
//   resumeCommand
// });
// assert.equal(snapshotCwdOnly.cwd, '/tmp/snapshot-cwd', 'snapshot cwd is the final fallback before empty');
// assert.equal(snapshotCwdOnly.worktreePath, null);
// assert.deepEqual(snapshotCwdOnly.preCommands, []);

// // (d) branchHint present -> branch === branchHint (and still preCommands []).
// const branchHinted = planConversationRestore({
//   session: { projectPath, branchHint: 'feature/tsk-346' },
//   snapshot: { branch: 'snapshot-branch' },
//   knownWorktreePaths: [],
//   resumeCommand
// });
// assert.equal(branchHinted.branch, 'feature/tsk-346', 'branchHint takes precedence');
// assert.deepEqual(branchHinted.preCommands, [], 'a branch hint never triggers a git switch');

// // snapshot branch is used only when no branchHint is given.
// const snapshotBranch = planConversationRestore({
//   session: { projectPath },
//   snapshot: { branch: 'snapshot-branch' },
//   knownWorktreePaths: [],
//   resumeCommand
// });
// assert.equal(snapshotBranch.branch, 'snapshot-branch', 'snapshot branch is used absent a branch hint');
// assert.deepEqual(snapshotBranch.preCommands, []);

// // (e) nothing set -> cwd === '' and branch null.
// const empty = planConversationRestore({
//   session: {},
//   snapshot: {},
//   knownWorktreePaths: [],
//   resumeCommand: ''
// });
// assert.equal(empty.cwd, '', 'no path inputs yields an empty cwd');
// assert.equal(empty.branch, null, 'no branch inputs yields a null branch');
// assert.equal(empty.worktreePath, null);
// assert.equal(empty.missingWorktree, false);
// assert.deepEqual(empty.preCommands, []);
// assert.equal(empty.resumeCommand, '', 'empty resume command passes through unchanged');

// // Invariant: preCommands is ALWAYS [] across every plan produced above.
// for (const plan of [
//   present,
//   missing,
//   missingEmptyKnown,
//   projectOnly,
//   snapshotCwdOnly,
//   branchHinted,
//   snapshotBranch,
//   empty
// ]) {
//   assert.deepEqual(plan.preCommands, [], 'preCommands must always be empty (no git-mutating commands)');
// }

// console.log('conversationWorkspaceRestore.test.mjs passed');
