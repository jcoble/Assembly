/**
 * worktreeAgentPrompts.test.ts — the Worktrees panel's three buttons, checked
 * as text rather than clicked.
 *
 * Two of the three start a session that does the work, and the only thing
 * standing between such a button and a folder full of somebody's unfinished work
 * is what that session is told to do first. So those prompts are the safety
 * mechanism, and this file reads them: explain before warning, warn before
 * removing, and never remove something risky without saying so.
 *
 * The third, Inspect, starts nothing at all any more. It hands the app's helper
 * model a block of facts and shows the answer under the row, so what this file
 * checks for it is that the facts are all there and that nothing in them is a
 * command anything could run.
 */
import assert from 'node:assert/strict';

import type { ProjectWorktree } from '../src/lib/tauriSource.ts';
import {
  buildWorktreeManagerRows,
  type WorktreeManagerRow
} from '../src/lib/shell/worktrees/worktreeManagerRows.ts';
import {
  describeWorktreeAgentQuestion,
  worktreeAgentActions,
  worktreeAgentPrompt,
  worktreeInspectFacts
} from '../src/lib/shell/panels/worktrees/worktreeAgentPrompts.ts';

const NOW = Date.UTC(2026, 7, 13, 18, 0, 0);
const PRIMARY = '/Users/dev/work/atlas';

function worktree(overrides: Partial<ProjectWorktree> = {}): ProjectWorktree {
  return {
    repo: 'atlas',
    path: '/Users/dev/work/worktrees/atlas/tsk-42-ledger',
    branch: 'tsk-42-ledger',
    taskID: null,
    isDirty: false,
    hasUnmergedCommits: false,
    isPrunable: false,
    prunableReason: null,
    isLocked: false,
    lockedReason: null,
    lastActivity: new Date(NOW - 3_600_000).toISOString(),
    deleteEligibility: 'safe',
    ...overrides
  };
}

/** One row, built the way the panel builds them so nothing here is hand-faked. */
function rowFor(...worktrees: ProjectWorktree[]): WorktreeManagerRow[] {
  return buildWorktreeManagerRows({
    worktrees: [worktree({ path: PRIMARY, branch: 'main' }), ...worktrees],
    repositories: [],
    sessions: [],
    primaryPath: PRIMARY,
    now: NOW
  });
}

// ── the three actions, in order ──────────────────────────────────────────────
{
  const rows = rowFor(worktree());
  const clean = rows.find((row) => !row.isPrimary);
  assert.ok(clean, 'the fixture has a non-primary row');

  const actions = worktreeAgentActions(clean);
  assert.deepEqual(
    actions.map((action) => action.id),
    ['inspect', 'archive-and-remove', 'remove'],
    'the panel offers exactly these three, in this order'
  );
  assert.ok(
    actions.every((action) => action.enabled),
    'a clean, removable worktree can be inspected, archived, or removed'
  );
  assert.equal(actions[0].destructive, false);
  assert.equal(actions[1].destructive, true);
  assert.equal(actions[2].destructive, true);
}

// ── the main checkout is never removed ───────────────────────────────────────
{
  const rows = rowFor(worktree());
  const primary = rows.find((row) => row.isPrimary);
  assert.ok(primary, 'the fixture has a primary row');

  const actions = worktreeAgentActions(primary);
  const byId = new Map(actions.map((action) => [action.id, action]));
  assert.equal(byId.get('inspect')?.enabled, true, 'the main checkout can still be looked at');
  for (const id of ['archive-and-remove', 'remove'] as const) {
    const action = byId.get(id);
    assert.ok(action);
    assert.equal(action.enabled, false, `${id} is switched off for the main checkout`);
    assert.ok(
      (action.disabledReason ?? '').trim().length > 0,
      `${id} says why it is switched off`
    );
  }
}

// ── a blocked row carries the reason the pane already worked out ─────────────
{
  const rows = rowFor(worktree({ isDirty: true, hasUnmergedCommits: true }));
  const blocked = rows.find((row) => !row.isPrimary);
  assert.ok(blocked);
  assert.equal(blocked.canRemove, false, 'the fixture is a row a careful remove refuses');
  assert.ok(blocked.blockedReason.length > 0);

  for (const action of worktreeAgentActions(blocked)) {
    if (action.id === 'inspect') {
      assert.equal(action.enabled, true, 'a blocked row is the one most worth inspecting');
      continue;
    }
    assert.equal(action.enabled, false);
    assert.equal(
      action.disabledReason,
      blocked.blockedReason,
      'the reason is the pane’s own, not a second opinion'
    );
  }
}

// ── inspect hands over facts, and nothing that could be run ─────────────────
{
  const rows = rowFor(worktree({ isDirty: true }));
  const row = rows.find((entry) => !entry.isPrimary);
  assert.ok(row);

  const facts = worktreeInspectFacts(row);
  assert.equal(typeof facts, 'string', 'it answers with text and does nothing else');

  assert.ok(facts.includes(`Branch: ${row.branch}`), 'the facts name the branch');
  assert.ok(facts.includes(row.path), 'the facts name the folder');
  assert.ok(facts.includes(`Last activity: ${row.age}`), 'the facts say when it was last touched');
  assert.ok(facts.includes(`Sessions here: ${row.sessionsLabel}`), 'the facts say who worked here');
  assert.ok(facts.includes('Remote:'), 'the facts say where the branch stands against its remote');
  assert.ok(
    facts.includes('Uncommitted changes: yes'),
    'a folder with work that was never committed says so'
  );

  for (const command of [row.commands.audit, row.commands.backup, row.commands.cleanup]) {
    assert.ok(!facts.includes(command), 'the facts carry nothing anything could run');
  }
}

// ── the facts are the same shape when there is nothing to report ─────────────
{
  const rows = rowFor(worktree());
  const row = rows.find((entry) => !entry.isPrimary);
  assert.ok(row);

  const facts = worktreeInspectFacts(row);
  assert.ok(facts.includes('Uncommitted changes: none'), 'a clean folder says so');
  assert.ok(
    facts.includes('Remote: not read'),
    'with no summary read, the facts say that rather than guessing at the counts'
  );
}

// ── remove warns before it removes ───────────────────────────────────────────
{
  const rows = rowFor(worktree());
  const row = rows.find((entry) => !entry.isPrimary);
  assert.ok(row);

  const prompt = worktreeAgentPrompt('remove', row, PRIMARY);
  assert.ok(prompt.prompt.includes(row.path), 'it names the folder');
  assert.ok(prompt.prompt.includes(row.branch), 'it names the branch');
  assert.ok(prompt.prompt.includes(row.commands.cleanup), 'it carries the removal command');
  assert.ok(prompt.prompt.includes(row.commands.audit), 'it looks before it removes');

  const text = prompt.prompt.toLowerCase();
  const risks = ['uncommitted', 'never pushed', 'unmerged', 'lock'];
  for (const risk of risks) {
    assert.ok(text.includes(risk), `it names ${risk} as something to warn about`);
  }
  assert.ok(
    text.includes('do not delete anything'),
    'it forbids deleting a risky finding without saying so first'
  );
  const explainAt = text.indexOf('explain');
  const removeAt = text.indexOf(row.commands.cleanup.toLowerCase());
  assert.ok(explainAt >= 0 && removeAt >= 0 && explainAt < removeAt, 'explaining comes first');
}

// ── archive and remove copies the work away before it removes anything ───────
{
  const rows = rowFor(worktree());
  const row = rows.find((entry) => !entry.isPrimary);
  assert.ok(row);

  const prompt = worktreeAgentPrompt('archive-and-remove', row, PRIMARY);
  assert.ok(prompt.prompt.includes(row.commands.backup), 'it carries the backup command');
  assert.ok(prompt.prompt.includes(row.commands.cleanup), 'it carries the removal command');
  assert.ok(
    prompt.prompt.indexOf(row.commands.backup) < prompt.prompt.indexOf(row.commands.cleanup),
    'the copy happens before the removal'
  );
  assert.ok(prompt.prompt.toLowerCase().includes('do not delete anything'));
}

// ── every prompt runs where the worktree is, and says which one it is ────────
{
  const rows = rowFor(worktree());
  const row = rows.find((entry) => !entry.isPrimary);
  assert.ok(row);

  for (const action of ['archive-and-remove', 'remove'] as const) {
    const prompt = worktreeAgentPrompt(action, row, PRIMARY);
    assert.equal(prompt.cwd, row.path, `${action} runs in the worktree`);
    assert.ok(prompt.title.includes(row.folderName), `${action} names the folder in its title`);
    assert.equal(prompt.projectPath, PRIMARY, `${action} belongs to the repository`);
  }

  // With no main checkout known, the worktree is the best repository we have.
  assert.equal(worktreeAgentPrompt('remove', row, null).projectPath, row.path);
}

// ── the question asked before a destructive session starts ──────────────────
{
  const rows = rowFor(worktree({ isDirty: true }));
  const risky = rows.find((entry) => !entry.isPrimary);
  assert.ok(risky);

  for (const action of ['archive-and-remove', 'remove'] as const) {
    const question = describeWorktreeAgentQuestion(action, risky);
    assert.ok(question, `${action} asks first`);
    assert.equal(question.destructive, true);
    assert.ok(question.title.includes(risky.folderName), 'the question names the folder');
    assert.ok(
      question.lines.some((line) => line.includes('never committed')),
      'the question says out loud that this folder has uncommitted work'
    );
    assert.ok(
      question.lines.some((line) => line.includes(risky.path)),
      'the question names the folder a session will be started in'
    );
  }

  const archive = describeWorktreeAgentQuestion('archive-and-remove', risky);
  assert.ok(archive?.lines.some((line) => line.includes('somewhere safe')));
}

console.log('worktreeAgentPrompts.test.ts passed');
