/**
 * worktreeManagerRows.ts — everything the worktree manager pane WORKS OUT,
 * with nothing it has to ask anybody.
 *
 * PURE: no runes, no DOM, no backend call, no clock of its own (`now` is always
 * a parameter). That is what lets `scripts/worktreeManager.test.mjs` run every
 * decision in this file under plain node.
 *
 * The pane shows one row per worktree, and a row is a join of three separate
 * things the desktop app already knows how to fetch, none of which arrives
 * joined:
 *
 *  1. the worktrees themselves (`list_project_worktrees`) — branch, task, dirty,
 *     unmerged, locked, prunable, last activity;
 *  2. the repository summaries (`list_git_repository_summaries`), which are the
 *     ONLY place the ahead/behind counts live — matched to a worktree by folder;
 *  3. the sessions the shell owns, matched to a worktree the same way, so a row
 *     can say whether anyone is still working in that folder.
 *
 * The joins are by folder path, and the two sides spell paths differently often
 * enough (one with a trailing slash, one without) that normalising is the whole
 * reason the join works — hence `normalizeWorktreePath` and its test.
 *
 * The safety verdict, the decision lane and the three copyable commands are NOT
 * decided here. They come from `worktreeSafety.ts`, which is a long-standing,
 * separately tested module; this file imports it and passes it the right
 * options. Re-deriving any of that here would be a second opinion nobody asked
 * for.
 */
import { formatLastActivity } from '../relativeTime.ts';
import type { GitRepositorySummary, ProjectWorktree } from '../../tauriSource.ts';
import {
  buildWorktreeSafetySummary,
  worktreeDecisionLane,
  type WorktreeDecisionLane,
  type WorktreeSafetySummary
} from '../../worktreeSafety.ts';

/** A session as this pane needs to see one: where it works and whether it runs. */
export interface WorktreeSessionInput {
  ownedId: string;
  title: string;
  /** The folder the session actually runs in. */
  cwd: string;
  /** The project it was opened for, used when there is no cwd. */
  projectPath: string | null;
  state: 'live' | 'background' | 'exited';
}

/** One session that has worked in a worktree. */
export interface WorktreeSessionLink {
  ownedId: string;
  title: string;
  /** Its terminal is running right now. */
  isRunning: boolean;
}

/** How loudly a chip should read. */
export type WorktreeChipTone = 'danger' | 'warn' | 'info';

/** One short word on a row, saying something that would stop a removal. */
export interface WorktreeChip {
  id: 'dirty' | 'unmerged' | 'locked' | 'prunable';
  label: string;
  /**
   * The same thing in as few characters as it can be said in, for the closed
   * row — where the branch, the chips and the buttons all share one line and
   * the full label would push the branch name off the end of it.
   */
  short: string;
  /** The longer sentence behind it, shown on hover. */
  title: string;
  tone: WorktreeChipTone;
}

/** How this branch stands against its remote. `null` means we did not read it. */
export interface WorktreeAheadBehind {
  ahead: number;
  behind: number;
  hasUpstream: boolean;
}

/** One row of the pane: a worktree with everything known about it attached. */
export interface WorktreeManagerRow {
  /** The worktree record exactly as the backend returned it. */
  worktree: ProjectWorktree;
  path: string;
  repo: string;
  branch: string;
  /** The folder on disk — the word a destructive removal asks you to type. */
  folderName: string;
  /** The task the backend linked this worktree to, or `null`. */
  taskId: string | null;
  /** This is the repository's main checkout. It is never removable. */
  isPrimary: boolean;
  /**
   * The folder is no longer on disk and only git's note about it is left.
   *
   * This row has nothing to back up and nothing to delete, so it gets one
   * action of its own ("Clear this entry") rather than the three that belong to
   * a worktree that still exists.
   */
  folderGone: boolean;
  /** "3h ago", or "no activity recorded". */
  age: string;
  /** The stamp the age was worked out from, for hovering. */
  lastActivity: string | null;
  chips: WorktreeChip[];
  aheadBehind: WorktreeAheadBehind | null;
  /** "2 commits to push", or "" when no summary was read for this folder. */
  aheadBehindLabel: string;
  sessions: WorktreeSessionLink[];
  sessionsLabel: string;
  safety: WorktreeSafetySummary;
  lane: WorktreeDecisionLane;
  /** The three copyable commands, straight from `worktreeSafety.ts`. */
  commands: { audit: string; backup: string; cleanup: string };
  /** An everyday remove would go through. */
  canRemove: boolean;
  /** Why an everyday remove would be refused; "" when it would not be. */
  blockedReason: string;
}

/** What the pane joins together to build its rows. */
export interface WorktreeManagerInput {
  worktrees: ProjectWorktree[];
  repositories: GitRepositorySummary[];
  sessions: WorktreeSessionInput[];
  /** The repository's main checkout, when it is known from somewhere else. */
  primaryPath: string | null;
  now?: number | Date;
}

// ── paths ────────────────────────────────────────────────────────────────────

/**
 * One spelling of a folder. Two sides of a join write the same folder with and
 * without a trailing slash, and around whitespace, often enough that comparing
 * them raw quietly loses rows.
 */
export function normalizeWorktreePath(value: string | null | undefined): string {
  const trimmed = (value ?? '').trim();
  if (trimmed === '') return '';
  if (trimmed === '/') return '/';
  return trimmed.replace(/\/+$/, '');
}

/** The last folder of a path: `/a/b/tsk-12-thing` → `tsk-12-thing`. */
export function worktreeFolderName(path: string | null | undefined): string {
  const parts = normalizeWorktreePath(path).split('/').filter(Boolean);
  return parts.length > 0 ? parts[parts.length - 1] : '';
}

/**
 * The repository's main checkout.
 *
 * `git worktree list` always prints the main working tree first, and the
 * backend keeps that order, so the first record is the answer. When there are
 * no records at all the folder we asked about is the best thing left to say.
 */
export function primaryCheckoutPath(
  worktrees: ProjectWorktree[],
  fallbackRoot: string | null | undefined
): string {
  const first = worktrees[0];
  if (first) return normalizeWorktreePath(first.path);
  return normalizeWorktreePath(fallbackRoot);
}

// ── ahead / behind ───────────────────────────────────────────────────────────

/**
 * The ahead/behind counts for one worktree, taken from the repository summaries.
 *
 * `null` means nobody read this folder — the pane then says nothing about a
 * remote, rather than showing "0 ahead, 0 behind", which reads as "in sync" and
 * would be a guess. A summary git failed to read is the same as no summary.
 */
export function findAheadBehind(
  worktree: ProjectWorktree,
  repositories: GitRepositorySummary[]
): WorktreeAheadBehind | null {
  const wanted = normalizeWorktreePath(worktree.path);
  if (!wanted) return null;
  const match = repositories.find(
    (summary) => normalizeWorktreePath(summary.path) === wanted && !summary.error
  );
  if (!match) return null;
  return {
    ahead: match.ahead ?? 0,
    behind: match.behind ?? 0,
    hasUpstream: Boolean(match.hasUpstream)
  };
}

function commits(count: number): string {
  return `${count} ${count === 1 ? 'commit' : 'commits'}`;
}

/** "2 commits to push, 1 commit to pull". "" when the counts are unknown. */
export function describeAheadBehind(counts: WorktreeAheadBehind | null): string {
  if (!counts) return '';
  if (!counts.hasUpstream) return 'This branch is not on any remote';
  const parts: string[] = [];
  if (counts.ahead > 0) parts.push(`${commits(counts.ahead)} to push`);
  if (counts.behind > 0) parts.push(`${commits(counts.behind)} to pull`);
  return parts.length === 0 ? 'Up to date with its remote' : parts.join(', ');
}

// ── sessions ─────────────────────────────────────────────────────────────────

/** Where a session works: its own folder first, the project it was opened for
 * second. A worktree session pointed at its project would join to the wrong row. */
function sessionFolder(session: WorktreeSessionInput): string {
  return normalizeWorktreePath(session.cwd) || normalizeWorktreePath(session.projectPath);
}

/** Every session that has worked in this worktree, each listed once. */
export function sessionsForWorktree(
  worktree: ProjectWorktree,
  sessions: WorktreeSessionInput[]
): WorktreeSessionLink[] {
  const wanted = normalizeWorktreePath(worktree.path);
  if (!wanted) return [];
  const seen = new Set<string>();
  const links: WorktreeSessionLink[] = [];
  for (const session of sessions) {
    if (sessionFolder(session) !== wanted) continue;
    if (seen.has(session.ownedId)) continue;
    seen.add(session.ownedId);
    links.push({
      ownedId: session.ownedId,
      title: session.title,
      isRunning: session.state === 'live'
    });
  }
  return links;
}

/** "2 sessions worked here, 1 still running". */
export function describeSessions(links: WorktreeSessionLink[]): string {
  if (links.length === 0) return 'No session has worked here';
  const running = links.filter((link) => link.isRunning).length;
  const head = `${links.length} ${links.length === 1 ? 'session' : 'sessions'} worked here`;
  if (running === 0) return head;
  if (running === links.length && running > 1) return `${head}, both still running`;
  return `${head}, ${running} still running`;
}

// ── age ──────────────────────────────────────────────────────────────────────

/**
 * How long ago anything happened in this worktree, said the way the session
 * rail says it. A stamp nobody recorded, and a stamp nothing can read, both come
 * back as the same plain sentence rather than as empty space.
 */
export function describeWorktreeAge(
  lastActivity: string | null | undefined,
  now: number | Date
): string {
  const when = now instanceof Date ? now : new Date(now);
  return formatLastActivity(lastActivity, when) || 'no activity recorded';
}

// ── chips ────────────────────────────────────────────────────────────────────

function isPrunable(worktree: ProjectWorktree): boolean {
  return worktree.isPrunable === true;
}

function isLocked(worktree: ProjectWorktree): boolean {
  return worktree.isLocked === true;
}

/**
 * The short words on a row. A worktree with nothing in the way wears none of
 * them — the absence of chips is itself the message that it is safe.
 */
export function worktreeChips(worktree: ProjectWorktree): WorktreeChip[] {
  const chips: WorktreeChip[] = [];
  if (worktree.isDirty) {
    chips.push({
      id: 'dirty',
      label: 'Uncommitted changes',
      short: 'Uncommitted',
      title: 'Files here have been changed and never committed.',
      tone: 'warn'
    });
  }
  if (worktree.hasUnmergedCommits) {
    chips.push({
      id: 'unmerged',
      label: 'Commits not pushed',
      short: 'Not pushed',
      title: 'This branch has commits that are on no remote.',
      tone: 'warn'
    });
  }
  if (isLocked(worktree)) {
    const reason = (worktree.lockedReason ?? '').trim();
    chips.push({
      id: 'locked',
      label: 'Locked',
      short: 'Locked',
      title: reason
        ? `Someone locked this worktree because: ${reason}.`
        : 'Someone locked this worktree, with no reason given.',
      tone: 'danger'
    });
  }
  if (isPrunable(worktree)) {
    const reason = (worktree.prunableReason ?? '').trim();
    chips.push({
      id: 'prunable',
      label: 'Folder is gone',
      short: 'Folder gone',
      title: reason
        ? `The folder is no longer on disk: ${reason}. Only git's note about it is left.`
        : "The folder is no longer on disk. Only git's note about it is left.",
      tone: 'info'
    });
  }
  return chips;
}

// ── would an everyday remove be refused ──────────────────────────────────────

/**
 * Why the everyday remove would refuse this worktree, in the same order the
 * desktop app checks. "" means it would go through.
 *
 * A worktree whose folder is already gone is NOT blocked: removing it only
 * tidies away the note git kept about it.
 */
export function safeRemoveBlockedReason(worktree: ProjectWorktree): string {
  if (isPrunable(worktree)) return '';
  const reasons: string[] = [];
  if (worktree.isDirty) reasons.push('It has changes that were never committed.');
  if (worktree.hasUnmergedCommits) {
    reasons.push('It has commits that were never pushed to a remote.');
  }
  if (isLocked(worktree)) reasons.push('It is locked.');
  return reasons.join(' ');
}

// ── the rows ─────────────────────────────────────────────────────────────────

function activityRank(lastActivity: string | null): number {
  if (!lastActivity) return Number.NEGATIVE_INFINITY;
  const parsed = Date.parse(lastActivity);
  return Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed;
}

/**
 * One row per worktree, in the order the pane shows them: the main checkout at
 * the top (it is the anchor, and the one thing that can never be removed), then
 * everything else with the most recently touched first. Worktrees nothing is
 * known about sink to the bottom, where a decision about them is least urgent.
 */
export function buildWorktreeManagerRows(input: WorktreeManagerInput): WorktreeManagerRow[] {
  const now = input.now ?? Date.now();
  const primaryPath = primaryCheckoutPath(input.worktrees, input.primaryPath);
  const activeSessionPaths = input.sessions
    .filter((session) => session.state === 'live')
    .map(sessionFolder);

  const rows = input.worktrees.map((worktree): WorktreeManagerRow => {
    const path = normalizeWorktreePath(worktree.path);
    const safety = buildWorktreeSafetySummary(worktree, {
      primaryPath,
      activeSessionPaths,
      now
    });
    const aheadBehind = findAheadBehind(worktree, input.repositories);
    const sessions = sessionsForWorktree(worktree, input.sessions);
    const isPrimary = path !== '' && path === primaryPath;
    const blockedReason = safeRemoveBlockedReason(worktree);
    const taskId = (worktree.taskID ?? '').trim();

    return {
      worktree,
      path,
      repo: worktree.repo,
      branch: worktree.branch,
      folderName: worktreeFolderName(worktree.path),
      taskId: taskId === '' ? null : taskId,
      isPrimary,
      folderGone: !isPrimary && isPrunable(worktree),
      age: describeWorktreeAge(worktree.lastActivity, now),
      lastActivity: worktree.lastActivity,
      chips: worktreeChips(worktree),
      aheadBehind,
      aheadBehindLabel: describeAheadBehind(aheadBehind),
      sessions,
      sessionsLabel: describeSessions(sessions),
      safety,
      lane: worktreeDecisionLane(safety),
      commands: {
        audit: safety.auditCommand,
        backup: safety.backupCommand,
        cleanup: safety.cleanupCommand
      },
      canRemove: !isPrimary && blockedReason === '',
      blockedReason
    };
  });

  return rows.sort((left, right) => {
    if (left.isPrimary !== right.isPrimary) return left.isPrimary ? -1 : 1;
    const byActivity = activityRank(right.lastActivity) - activityRank(left.lastActivity);
    if (byActivity !== 0 && Number.isFinite(byActivity)) return byActivity;
    if (activityRank(left.lastActivity) !== activityRank(right.lastActivity)) {
      return activityRank(left.lastActivity) === Number.NEGATIVE_INFINITY ? 1 : -1;
    }
    return left.branch.localeCompare(right.branch);
  });
}

/** The one line above the list: how many worktrees, and how much deciding is left. */
export function summarizeWorktreeManager(rows: WorktreeManagerRow[]): string {
  if (rows.length === 0) return 'No worktrees';
  const primary = rows.filter((row) => row.isPrimary).length;
  const safe = rows.filter((row) => !row.isPrimary && row.canRemove).length;
  const deciding = rows.length - primary - safe;
  const parts: string[] = [];
  if (primary > 0) parts.push(`${primary} main checkout`);
  if (safe > 0) parts.push(`${safe} safe to remove`);
  if (deciding > 0) parts.push(`${deciding} need${deciding === 1 ? 's' : ''} a decision`);
  const head = `${rows.length} ${rows.length === 1 ? 'worktree' : 'worktrees'}`;
  return parts.length === 0 ? head : `${head}: ${parts.join(', ')}`;
}

/**
 * Exactly what a forced removal destroys, one sentence per thing, for the
 * dialog that asks whether to go through with it.
 *
 * Written as consequences, not as conditions: the reader is about to press a
 * button, so every line says what will be gone afterwards. A worktree with
 * nothing to lose still gets a line — a dialog that lists nothing reads like it
 * does nothing.
 */
export function describeForcedRemoval(row: WorktreeManagerRow): string[] {
  const lines: string[] = [];
  if (row.worktree.isDirty) {
    lines.push('Deletes every change in this folder that was never committed.');
  }
  if (row.worktree.hasUnmergedCommits) {
    const ahead = row.aheadBehind?.ahead ?? 0;
    lines.push(
      ahead > 0
        ? `Deletes ${commits(ahead)} on this branch that never reached a remote.`
        : 'Deletes the commits on this branch that never reached a remote.'
    );
  }
  if (isLocked(row.worktree)) {
    const reason = (row.worktree.lockedReason ?? '').trim();
    lines.push(
      reason
        ? `Unlocks it first — it was locked because: ${reason}.`
        : 'Unlocks it first — it was locked, with no reason given.'
    );
  }
  if (row.sessions.length > 0) {
    const running = row.sessions.filter((link) => link.isRunning).length;
    const count = `${row.sessions.length} ${row.sessions.length === 1 ? 'session' : 'sessions'}`;
    lines.push(
      running > 0
        ? `${count} still point at this folder, and ${running === 1 ? 'one is' : `${running} are`} running right now.`
        : `${count} worked in this folder and will point at nothing afterwards.`
    );
  }
  if (lines.length === 0) {
    lines.push(`Deletes the folder ${row.path} and the note git keeps about it.`);
  }
  return lines;
}

// ── the facts under an open row ──────────────────────────────────────────────

/** One labelled fact in an open row's detail block. */
export interface WorktreeFact {
  label: string;
  value: string;
  /** The raw timestamp behind a relative one, for hovering. '' when there is none. */
  stamp: string;
}

/**
 * What an open row says about itself, as labelled facts.
 *
 * These used to run along the CLOSED row as one line with "·" between them,
 * which is how a row with nothing to say ended up reading "· / no activity
 * recorded / · / No session has worked here" stacked over four lines in a narrow
 * pane. A fact with nothing in it is left out here instead, so there is never a
 * separator with empty space on both sides of it.
 */
export function worktreeFacts(row: WorktreeManagerRow): WorktreeFact[] {
  const facts: WorktreeFact[] = [];
  if (row.path) {
    facts.push({
      label: 'Folder',
      value: row.folderGone ? `${row.path} — not on disk any more` : row.path,
      stamp: ''
    });
  }
  facts.push({ label: 'Last activity', value: row.age, stamp: row.lastActivity ?? '' });
  if (row.aheadBehindLabel) {
    facts.push({ label: 'Remote', value: row.aheadBehindLabel, stamp: '' });
  }
  facts.push({ label: 'Sessions', value: row.sessionsLabel, stamp: '' });
  return facts;
}

// ── the question asked before anything is removed ────────────────────────────

/** The three removals this pane can start. Each asks its own question first. */
export type WorktreeRemovalKind = 'remove' | 'clear' | 'force';

/**
 * Whether the desktop app has said it can do a particular thing. Spelled out
 * here rather than imported from the store, so this file stays pure.
 */
export type WorktreeCapabilityState = 'unknown' | 'available' | 'unavailable';

/** Everything the confirmation dialog needs to say and do. */
export interface WorktreeRemovalQuestion {
  kind: WorktreeRemovalKind;
  title: string;
  /** The paragraph under the title. */
  intro: string;
  /** One sentence per thing that will happen, in the order it matters. */
  lines: string[];
  /** The word on the button that goes through with it. */
  confirmLabel: string;
  /** The word on the button that does not. */
  cancelLabel: string;
  /** The folder's own name has to be typed before the button works. */
  requiresTypedName: boolean;
  /** This one can lose work, so its button is drawn in the danger colour. */
  destructive: boolean;
}

/** What the pane knows when it asks the question. */
export interface WorktreeRemovalContext {
  kind: WorktreeRemovalKind;
  /** Whether this build can clear ONE folder-gone row instead of all of them. */
  pruneSingleRow: WorktreeCapabilityState;
  /** The branches of the OTHER rows whose folder is gone, for the honest count. */
  otherFolderGoneBranches: string[];
}

function listBranches(branches: string[]): string {
  const named = branches.slice(0, 4);
  const rest = branches.length - named.length;
  const quoted = named.map((branch) => `“${branch}”`).join(', ');
  return rest > 0 ? `${quoted} and ${rest} more` : quoted;
}

/**
 * What clearing a folder-gone row would take with it besides that row.
 *
 * This is the sentence a real person needed and did not get: they pressed the
 * button on one row whose folder was gone, and BOTH of their stale rows
 * disappeared, because the desktop app answered by running git's repo-wide
 * tidy-up. An app build that can clear one row says so; one that cannot says
 * that, plainly, before anything happens.
 */
function collateralClearLines(context: WorktreeRemovalContext): string[] {
  const others = context.otherFolderGoneBranches.filter((branch) => branch.trim() !== '');
  const alsoCleared =
    others.length === 0
      ? []
      : [
          `Right now that would also clear ${others.length} other ${
            others.length === 1 ? 'row' : 'rows'
          }: ${listBranches(others)}.`
        ];

  if (context.pruneSingleRow === 'available') {
    return ['Only this row is cleared. Every other row is left exactly as it is.'];
  }
  if (context.pruneSingleRow === 'unknown') {
    return [
      'The app has not answered yet whether it can clear this row on its own. If it cannot, clearing this row will also clear every other row whose folder is gone.',
      ...alsoCleared
    ];
  }
  return [
    'Because this app build is older, clearing this row will also clear every other row whose folder is gone.',
    ...alsoCleared
  ];
}

/**
 * The whole confirmation dialog, worked out here so it can be read in a test
 * rather than clicked through in an app.
 *
 * Every removal path goes through one of these — including the folder-gone row,
 * which used to act on the first click with nothing asked.
 */
export function describeRemovalQuestion(
  row: WorktreeManagerRow,
  context: WorktreeRemovalContext
): WorktreeRemovalQuestion {
  if (context.kind === 'clear') {
    return {
      kind: 'clear',
      title: `Clear git’s record of “${row.branch}”?`,
      intro:
        'The folder is already gone. This only clears git’s records for it — nothing on disk is touched.',
      lines: [
        `Removes the note git keeps that a worktree for this branch used to be at ${row.path}.`,
        `The branch “${row.branch}” itself stays in the repository.`,
        ...collateralClearLines(context)
      ],
      confirmLabel: 'Clear this entry',
      cancelLabel: 'Leave it alone',
      requiresTypedName: false,
      destructive: false
    };
  }

  if (context.kind === 'force') {
    return {
      kind: 'force',
      title: `Delete the folder “${row.folderName}” and everything left in it?`,
      intro: `The branch “${row.branch}” stays in the repository. The folder on disk does not, and neither does anything below.`,
      lines: describeForcedRemoval(row),
      confirmLabel: 'Delete it',
      cancelLabel: 'Keep it',
      requiresTypedName: true,
      destructive: true
    };
  }

  const lines = [
    `Deletes the folder ${row.path} from your disk.`,
    'Nothing in it has been changed without being committed, and nothing is locked — the app checks again as it goes, and refuses if that has changed since this list was read.',
    `The branch “${row.branch}” stays in the repository.`
  ];
  const ahead = row.aheadBehind?.ahead ?? 0;
  if (ahead > 0) {
    lines.push(`This branch still has ${commits(ahead)} that its remote does not have.`);
  }
  if (row.sessions.length > 0) {
    const running = row.sessions.filter((link) => link.isRunning).length;
    const count = `${row.sessions.length} ${row.sessions.length === 1 ? 'session' : 'sessions'}`;
    lines.push(
      running > 0
        ? `${count} still point at this folder, and ${running === 1 ? 'one is' : `${running} are`} running right now.`
        : `${count} worked in this folder and will point at nothing afterwards.`
    );
  }

  return {
    kind: 'remove',
    title: `Remove the worktree “${row.branch}”?`,
    intro:
      'This is the careful remove: the desktop app stops and says so if it finds anything here that was never saved anywhere else.',
    lines,
    confirmLabel: 'Remove it',
    cancelLabel: 'Keep it',
    requiresTypedName: false,
    destructive: false
  };
}

/** The branches of every OTHER row whose folder is gone, for the clear dialog. */
export function otherFolderGoneBranches(
  rows: WorktreeManagerRow[],
  row: WorktreeManagerRow
): string[] {
  return rows
    .filter((other) => other.folderGone && other.path !== row.path)
    .map((other) => other.branch);
}

/** The rows whose branch, task, folder or path contains what was typed. */
export function filterWorktreeRows(
  rows: WorktreeManagerRow[],
  query: string
): WorktreeManagerRow[] {
  const needle = (query ?? '').trim().toLowerCase();
  if (needle === '') return rows;
  return rows.filter((row) =>
    [row.branch, row.taskId ?? '', row.folderName, row.path, row.repo]
      .join(' ')
      .toLowerCase()
      .includes(needle)
  );
}
