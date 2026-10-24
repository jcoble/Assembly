// import assert from 'node:assert/strict';
// import { agentSessionFocusLane } from '../src/lib/agentSessionFocus.ts';

// const session = {
//   provider: 'codex',
//   id: '019c-session',
//   title: 'Review checkout flow',
//   model: 'gpt-5.5 xhigh',
//   projectPath: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center',
//   lastActivity: '2026-06-12T12:00:00Z',
//   resumeCommands: ['codex resume 019c-session']
// };

// assert.deepEqual(
//   agentSessionFocusLane(session, {
//     kind: 'live-terminal',
//     tone: 'ready',
//     label: 'Live terminal',
//     detail: 'Can reattach to the saved embedded terminal session.',
//     canRestoreWorkspace: true,
//     canResumeEmbedded: true
//   }),
//   {
//     action: 'reattach',
//     label: 'Reattach',
//     tone: 'ready',
//     title: 'Reattach saved terminal',
//     detail: 'Can reattach to the saved embedded terminal session.'
//   }
// );

// assert.deepEqual(
//   agentSessionFocusLane(session, {
//     kind: 'missing-worktree',
//     tone: 'blocked',
//     label: 'Worktree missing',
//     detail: 'Saved worktree is not in the current worktree scan; restore files cautiously.',
//     repairLabel: 'Copy repair plan',
//     repairDetail: 'Audit Git worktree metadata before resuming.',
//     canRestoreWorkspace: true,
//     canResumeEmbedded: false
//   }),
//   {
//     action: 'repair',
//     label: 'Repair',
//     tone: 'blocked',
//     title: 'Copy repair plan',
//     detail: 'Saved worktree is not in the current worktree scan; restore files cautiously.'
//   }
// );

// assert.deepEqual(agentSessionFocusLane(session, null), {
//   action: 'resume',
//   label: 'Resume',
//   tone: 'warning',
//   title: 'Resume session without saved workspace',
//   detail: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-127-command-center'
// });

// assert.deepEqual(agentSessionFocusLane({ ...session, projectPath: null, resumeCommands: [] }, null), {
//   action: 'save-workspace',
//   label: 'Save',
//   tone: 'neutral',
//   title: 'Save workspace before switching',
//   detail: 'No saved workspace or resume command'
// });
