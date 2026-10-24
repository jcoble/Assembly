/**
 * worktreeAgentPrompts.ts — what the Worktrees panel's three buttons say.
 *
 * The panel used to print three commands and ask the reader to paste them into
 * a terminal themselves. That was the safe thing to do while nothing in the app
 * could be trusted to look before it deleted. Now the two removal buttons start
 * a session in the worktree and hand it one of these prompts, so the same
 * commands are still the substance — they are just carried by something that
 * reads the folder first and says what it found.
 *
 * The third button, Inspect, starts nothing. Looking at a folder does not need
 * a whole session in it: `worktreeInspectFacts` writes down what this list
 * already knows and the panel asks the app's own small utility model about it,
 * which reads nothing and changes nothing.
 *
 * WHY THE PROMPT IS THE SAFETY MECHANISM: a worktree is somebody's unfinished
 * work until proven otherwise, and the proof lives on disk, not in this list.
 * The list is a snapshot that was read some seconds ago; the session reads the
 * folder as it is now. So both removal prompts are written in the same four
 * steps, in this order, and the order is the whole point:
 *
 *   1. look at the folder, with a command that changes nothing;
 *   2. explain in plain English what is in it;
 *   3. warn about anything that would be lost — changes never committed,
 *      commits never pushed, a branch never merged, a lock somebody set;
 *   4. only then do the thing that was asked, and say exactly what was done.
 *
 * Both say out loud that nothing risky may be deleted without being described
 * first. Neither of them is allowed to be quiet about a loss.
 *
 * PURE: no runes, no DOM, no backend call. Everything comes from the row the
 * panel already built, which is what lets `scripts/worktreeAgentPrompts.test.ts`
 * read every word of these under plain node.
 */
import type { WorktreeManagerRow } from '../../worktrees/worktreeManagerRows.ts';

/** The three things the panel can do about one worktree. */
export type WorktreeAgentActionId = 'inspect' | 'archive-and-remove' | 'remove';

/** The two that start a session. Inspect is answered by the helper instead. */
export type WorktreeSessionActionId = Exclude<WorktreeAgentActionId, 'inspect'>;

/** Everything needed to start the session one button starts. */
export interface WorktreeAgentPrompt {
  /** The session title shown in the left rail, e.g. "Inspect tsk-42-ledger". */
  title: string;
  /** The first message sent to the session. */
  prompt: string;
  /** The worktree folder the session runs in. */
  cwd: string;
  /** The repository the worktree belongs to. */
  projectPath: string;
}

/** One button: what it says, whether it can be pressed, and why not. */
export interface WorktreeAgentAction {
  id: WorktreeAgentActionId;
  label: string;
  enabled: boolean;
  disabledReason: string | null;
  /** It can lose work, so it is drawn in the danger colour and asked about. */
  destructive: boolean;
}

/** Shown on both removal buttons for the repository's own checkout. */
const PRIMARY_CHECKOUT_REASON = "This is the repository's main checkout.";

/** The last-resort reason, so a switched-off button is never silent about it. */
const UNKNOWN_BLOCK_REASON = 'The app will not remove this worktree.';

const LABELS: Record<WorktreeAgentActionId, string> = {
  inspect: 'Inspect',
  'archive-and-remove': 'Archive & Remove',
  remove: 'Remove'
};

/**
 * The three buttons for one row, in the order they are drawn.
 *
 * Inspect is never switched off: a row nobody may remove is the row most worth
 * looking at, and looking changes nothing.
 */
export function worktreeAgentActions(row: WorktreeManagerRow): WorktreeAgentAction[] {
  const blocked = row.isPrimary
    ? PRIMARY_CHECKOUT_REASON
    : row.canRemove
      ? null
      : row.blockedReason.trim() || UNKNOWN_BLOCK_REASON;

  return [
    {
      id: 'inspect',
      label: LABELS.inspect,
      enabled: true,
      disabledReason: null,
      destructive: false
    },
    {
      id: 'archive-and-remove',
      label: LABELS['archive-and-remove'],
      enabled: blocked === null,
      disabledReason: blocked,
      destructive: true
    },
    {
      id: 'remove',
      label: LABELS.remove,
      enabled: blocked === null,
      disabledReason: blocked,
      destructive: true
    }
  ];
}

/**
 * What this list believes about the worktree, handed over as something to
 * confirm rather than as something to rely on. The session reads the folder
 * itself; this is only so it knows what it is expected to find.
 */
function knownFacts(row: WorktreeManagerRow): string {
  const lines = [
    `- Branch: ${row.branch}`,
    `- Repository: ${row.repo}`,
    `- Folder: ${row.path}`,
    `- Last activity: ${row.age}`,
    `- Sessions: ${row.sessionsLabel}`
  ];
  if (row.aheadBehindLabel) lines.push(`- Remote: ${row.aheadBehindLabel}`);
  if (row.folderGone) lines.push('- The folder is no longer on disk; only the git record is left.');
  lines.push(`- Verdict so far: ${row.safety.reason}. ${row.safety.recommendation}`);
  lines.push(
    row.blockedReason
      ? `- A careful remove would be refused: ${row.blockedReason}`
      : '- A careful remove would go through.'
  );
  return lines.join('\n');
}

/**
 * What the utility model is told about one worktree, as plain lines.
 *
 * The helper cannot read anything: it sees these sentences and nothing else, so
 * everything the answer can be based on has to be here. That also makes this
 * the whole of what Inspect does — it returns text, starts no session, touches
 * no folder, and runs none of the commands the other two buttons carry.
 *
 * "Remote: not read" is deliberate. An unread folder and a folder in step with
 * its remote are different things, and printing "0 ahead, 0 behind" for the
 * first would be a guess dressed up as a fact.
 */
export function worktreeInspectFacts(row: WorktreeManagerRow): string {
  return [
    'Should this git worktree be kept or removed, and what should be done with it next?',
    '',
    `Branch: ${row.branch}`,
    `Folder: ${row.folderGone ? `${row.path} — not on disk any more` : row.path}`,
    `Remote: ${row.aheadBehindLabel || 'not read'}`,
    `Uncommitted changes: ${row.worktree.isDirty ? 'yes' : 'none'}`,
    `Last activity: ${row.age}`,
    `Sessions here: ${row.sessionsLabel}`
  ].join('\n');
}

/** Step 1 and step 2, written the same way for both removals. */
function lookAndExplain(row: WorktreeManagerRow): string {
  return [
    '1. Inspect the worktree first. This reads it and changes nothing:',
    '',
    row.commands.audit,
    '',
    '2. Explain in plain English what is in this worktree: what the branch was for, what has',
    '   been changed, what has been committed, and whether any of it exists anywhere else.'
  ].join('\n');
}

/** Step 3: the sentence that names every way this folder could lose work. */
function warnAboutRisk(): string {
  return [
    '3. Warn about anything risky before going further: uncommitted changes, commits that were',
    '   never pushed to a remote, an unmerged branch, a lock somebody set, or a session still',
    '   working in the folder. Say what you found and what would be lost.'
  ].join('\n');
}

/** The line both destructive prompts end their warning step with. */
const NO_QUIET_DELETION =
  'Do not delete anything you found risky without saying so first and saying exactly what would\n' +
  'be lost. If you are unsure whether work here exists anywhere else, stop and say so instead of\n' +
  'removing it.';

function removalPrompt(row: WorktreeManagerRow, archiveFirst: boolean): string {
  const steps = [lookAndExplain(row), warnAboutRisk(), NO_QUIET_DELETION, ''];
  let step = 4;

  if (archiveFirst) {
    steps.push(
      `${step}. Copy the work somewhere safe before anything is removed:`,
      '',
      row.commands.backup,
      ''
    );
    step += 1;
  }

  steps.push(
    `${step}. Only once steps 2 and 3 have been said out loud, remove the worktree:`,
    '',
    row.commands.cleanup,
    '',
    '   Git refuses this while there is work in the folder it cannot account for. That refusal is',
    '   the point: do not force past it until you have said what would be lost and confirmed a',
    '   copy of it exists somewhere else.',
    ''
  );
  step += 1;

  steps.push(
    `${step}. Report exactly what you did, what was removed, and where anything you saved went.`,
    '   If you decided not to remove it, say that and say why.'
  );

  const opening = archiveFirst
    ? `Archive and then remove the git worktree at ${row.path} on branch ${row.branch} — but only after`
    : `Remove the git worktree at ${row.path} on branch ${row.branch} — but only after`;

  return [
    `${opening} you have looked at it and explained what is there.`,
    '',
    'What this list believes about it, for you to confirm rather than trust:',
    knownFacts(row),
    '',
    ...steps
  ].join('\n');
}

/** The question asked before a button starts a session that can remove work. */
export interface WorktreeAgentQuestion {
  title: string;
  /** The paragraph under the title. */
  intro: string;
  /** One sentence per thing that will happen, in the order it matters. */
  lines: string[];
  confirmLabel: string;
  cancelLabel: string;
  /** Kept so the dialog can ask for the folder name where a path ever needs it. */
  requiresTypedName: boolean;
  destructive: boolean;
}

/** What this row could lose, said as consequences rather than as conditions. */
function riskLines(row: WorktreeManagerRow): string[] {
  const lines: string[] = [];
  if (row.worktree.isDirty) lines.push('This folder has changes that were never committed.');
  if (row.worktree.hasUnmergedCommits) {
    lines.push('This branch has commits that never reached a remote.');
  }
  if (row.worktree.isLocked === true) lines.push('Somebody locked this worktree.');
  if (row.sessions.length > 0) {
    const running = row.sessions.filter((link) => link.isRunning).length;
    lines.push(
      running > 0
        ? `${row.sessionsLabel}, so removing the folder would interrupt work in progress.`
        : `${row.sessionsLabel}, and those sessions would point at nothing afterwards.`
    );
  }
  return lines;
}

/**
 * What to ask before starting one of the two destructive sessions.
 *
 * Inspect is not one of them: it starts nothing, and a dialog in front of a
 * read is a dialog people learn to dismiss without reading.
 */
export function describeWorktreeAgentQuestion(
  action: WorktreeSessionActionId,
  row: WorktreeManagerRow
): WorktreeAgentQuestion {
  const archiveFirst = action === 'archive-and-remove';

  const lines = [
    `Starts a session in ${row.path}, which appears in the sessions list like any other.`,
    'It reads the worktree first and says in plain English what it found.',
    'It warns about anything risky before it removes anything, and stops rather than deleting work it cannot account for.'
  ];
  if (archiveFirst) lines.push('It copies the work somewhere safe before removing the folder.');
  lines.push(...riskLines(row));
  lines.push(`The branch “${row.branch}” stays in the repository either way.`);

  return {
    title: archiveFirst
      ? `Start a session to archive and remove “${row.folderName}”?`
      : `Start a session to remove “${row.folderName}”?`,
    intro:
      'Nothing is removed by pressing this. A session starts, looks at the folder, and reports before it acts — you can read what it says and stop it.',
    lines,
    confirmLabel: 'Start it',
    cancelLabel: 'Leave it alone',
    requiresTypedName: false,
    destructive: true
  };
}

/**
 * The session one button starts: where it runs, what it is called, and what it
 * is told to do.
 *
 * `primaryPath` is the repository's main checkout. Without one, the worktree is
 * the best repository we can name — better than claiming a folder we have not
 * been given.
 */
export function worktreeAgentPrompt(
  action: WorktreeSessionActionId,
  row: WorktreeManagerRow,
  primaryPath: string | null
): WorktreeAgentPrompt {
  const titles: Record<WorktreeSessionActionId, string> = {
    'archive-and-remove': `Archive and remove ${row.folderName}`,
    remove: `Remove ${row.folderName}`
  };

  return {
    title: titles[action],
    prompt: removalPrompt(row, action === 'archive-and-remove'),
    cwd: row.path,
    projectPath: (primaryPath ?? '').trim() || row.path
  };
}
